using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Persistence
    {
        private const int SchemaVersion = 1;
        private const int BucketsPerWindow = 96;

        internal static void SaveToCity(EntityManager entityManager, Entity city)
        {
            if (city == Entity.Null)
                return;

            if (!entityManager.HasBuffer<PassengerFlowStateElement>(city))
                entityManager.AddBuffer<PassengerFlowStateElement>(city);

            DynamicBuffer<PassengerFlowStateElement> buffer = entityManager.GetBuffer<PassengerFlowStateElement>(city);
            PassengerFlowPersistentState state = Capture();
            if (state == null)
            {
                buffer.Clear();
                return;
            }

            string payload = Workbenches.Json.Write(state);
            Write(buffer, Workbenches.Buffer.Split(payload));
        }

        internal static bool RestoreFromCity(EntityManager entityManager, Entity city)
        {
            if (city == Entity.Null || !entityManager.HasBuffer<PassengerFlowStateElement>(city))
                return false;

            DynamicBuffer<PassengerFlowStateElement> buffer = entityManager.GetBuffer<PassengerFlowStateElement>(city, true);
            if (buffer.Length == 0)
                return false;

            string payload = Read(buffer);
            if (string.IsNullOrEmpty(payload))
                return false;

            PassengerFlowPersistentState persisted = Workbenches.Json.Read<PassengerFlowPersistentState>(payload);
            Restore(persisted);
            return IsSupported(persisted);
        }

        internal static PassengerFlowPersistentState Capture()
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return null;

            Port port = Runtime.Current;
            return new PassengerFlowPersistentState
            {
                schemaVersion = SchemaVersion,
                bucketMinutes = Snapshot.BucketMinutes,
                serviceDayIndex = state.ServiceDayIndex,
                lastDayMinute = state.LastDayMinute,
                currentAbsoluteBucketIndex = SamplingSystem.AbsoluteBucketIndex(state.CurrentBucket),
                currentBucketServiceDayIndex = state.CurrentBucket.ServiceDayIndex,
                currentBucketStartMinute = state.CurrentBucket.BucketStartMinute,
                stationCatalog = state.Anchors.ExportCatalog(port),
                stationVolumes = state.Aggregates.ExportStationVolumes(),
                sectionVolumes = state.Aggregates.ExportSectionVolumes(),
                odFlows = state.Aggregates.ExportOdFlows(),
                warnings = state.Aggregates.ExportWarnings()
            };
        }

        internal static void Restore(PassengerFlowPersistentState persisted)
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return;

            state.Clear();
            if (!IsSupported(persisted))
                return;

            int currentAbsoluteBucket = ResolveCurrentAbsoluteBucket(persisted);
            int minAbsoluteBucket = Math.Max(0, currentAbsoluteBucket - (BucketsPerWindow - 1));
            PassengerFlowPersistentState trimmed = Trim(persisted, minAbsoluteBucket, currentAbsoluteBucket);

            state.ServiceDayIndex = Math.Max(0, persisted.serviceDayIndex);
            state.LastDayMinute = persisted.lastDayMinute;
            state.CurrentBucket = new TimeBucketKey(
                persisted.currentBucketServiceDayIndex,
                persisted.currentBucketStartMinute);
            state.CurrentAbsoluteBucketIndex = currentAbsoluteBucket;
            state.Anchors.RestoreCatalog(trimmed.stationCatalog);
            state.Aggregates.Restore(
                trimmed.stationVolumes,
                trimmed.sectionVolumes,
                trimmed.odFlows,
                trimmed.warnings);

            for (int absoluteBucket = minAbsoluteBucket; absoluteBucket <= currentAbsoluteBucket; absoluteBucket++)
                state.RollingWindow.Add(SamplingSystem.BucketFromAbsoluteIndex(absoluteBucket));

            SamplingSystem.TrimRollingWindowForRestore(state);
        }

        private static int ResolveCurrentAbsoluteBucket(PassengerFlowPersistentState persisted)
        {
            if (persisted == null)
                return 0;

            if (persisted.currentAbsoluteBucketIndex > 0)
                return persisted.currentAbsoluteBucketIndex;

            return SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(
                persisted.currentBucketServiceDayIndex,
                persisted.currentBucketStartMinute));
        }

        private static bool IsSupported(PassengerFlowPersistentState persisted)
        {
            if (persisted == null || persisted.schemaVersion != SchemaVersion)
                return false;

            if (persisted.bucketMinutes != Snapshot.BucketMinutes)
                return false;

            if (persisted.serviceDayIndex < 0
                || persisted.lastDayMinute < -1
                || persisted.lastDayMinute >= 1440
                || !IsValidBucket(persisted.currentBucketServiceDayIndex, persisted.currentBucketStartMinute))
            {
                return false;
            }

            int computedAbsoluteBucket = SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(
                persisted.currentBucketServiceDayIndex,
                persisted.currentBucketStartMinute));
            return persisted.currentAbsoluteBucketIndex <= 0
                || persisted.currentAbsoluteBucketIndex == computedAbsoluteBucket;
        }

        private static bool IsValidBucket(int serviceDayIndex, int bucketStartMinute)
        {
            return serviceDayIndex >= 0
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
                serviceDayIndex = persisted.serviceDayIndex,
                lastDayMinute = persisted.lastDayMinute,
                currentAbsoluteBucketIndex = maxAbsoluteBucket,
                currentBucketServiceDayIndex = persisted.currentBucketServiceDayIndex,
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
                warnings = (persisted.warnings ?? Array.Empty<PassengerFlowPersistedWarning>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray()
            };
        }

        private static bool IsWithinWindow(PassengerFlowPersistedBucketRow row, int minAbsoluteBucket, int maxAbsoluteBucket)
        {
            if (row == null)
                return false;

            if (!IsValidBucket(row.serviceDayIndex, row.bucketStartMinute))
                return false;

            int absoluteBucket = SamplingSystem.AbsoluteBucketIndex(
                new TimeBucketKey(row.serviceDayIndex, row.bucketStartMinute));
            return absoluteBucket >= minAbsoluteBucket && absoluteBucket <= maxAbsoluteBucket;
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
    }

    [DataContract]
    public sealed class PassengerFlowPersistentState
    {
        [DataMember] public int schemaVersion;
        [DataMember] public int bucketMinutes;
        [DataMember] public int serviceDayIndex;
        [DataMember] public int lastDayMinute;
        [DataMember] public int currentAbsoluteBucketIndex;
        [DataMember] public int currentBucketServiceDayIndex;
        [DataMember] public int currentBucketStartMinute;
        [DataMember] public PassengerFlowPersistedStationCatalog[] stationCatalog;
        [DataMember] public PassengerFlowPersistedStationVolume[] stationVolumes;
        [DataMember] public PassengerFlowPersistedSectionVolume[] sectionVolumes;
        [DataMember] public PassengerFlowPersistedOdFlow[] odFlows;
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
        [DataMember] public string mode;
        [DataMember] public int serviceDayIndex;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedStationVolume : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string lineId;
        [DataMember] public int stationSakIndex;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public int throughPassengersSum;
        [DataMember] public int throughSampleCount;
        [DataMember] public int waitingPassengersSnapshot;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedSectionVolume : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string lineId;
        [DataMember] public int fromStationSakIndex;
        [DataMember] public int toStationSakIndex;
        [DataMember] public int loadPassengersSum;
        [DataMember] public int sampleCount;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedOdFlow : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string firstLineId;
        [DataMember] public string lastLineId;
        [DataMember] public int originStationSakIndex;
        [DataMember] public int destinationStationSakIndex;
        [DataMember] public int completedCount;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedWarning : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string code;
        [DataMember] public string lineId;
        [DataMember] public int stationSakIndex;
        [DataMember] public int count;
        [DataMember] public uint lastFrame;
    }
}
