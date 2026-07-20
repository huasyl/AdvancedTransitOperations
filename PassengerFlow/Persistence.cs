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
        private const int SchemaVersion = 2;
        private const int BucketsPerWindow = 96;

        internal static void SaveToCity(EntityManager entityManager, Entity city)
        {
            if (city == Entity.Null)
            {
                Diagnostics.Log("PassengerFlowPersistSave", "result=skip reason=cityNull");
                return;
            }

            if (!entityManager.HasBuffer<PassengerFlowStateElement>(city))
            {
                entityManager.AddBuffer<PassengerFlowStateElement>(city);
                Diagnostics.Log("PassengerFlowPersistSave", "action=createBuffer city=" + Diagnostics.DescribeEntity(city));
            }

            DynamicBuffer<PassengerFlowStateElement> buffer = entityManager.GetBuffer<PassengerFlowStateElement>(city);
            PassengerFlowPersistentState state = Capture();
            if (state == null)
            {
                buffer.Clear();
                Diagnostics.Log("PassengerFlowPersistSave", "result=cleared reason=captureNull city=" + Diagnostics.DescribeEntity(city));
                return;
            }

            string payload = Workbenches.Json.Write(state);
            List<string> chunks = Workbenches.Buffer.Split(payload);
            Write(buffer, chunks);
            if (Diagnostics.Enabled)
            {
                Diagnostics.Log(
                    "PassengerFlowPersistSave",
                    "result=saved city=" + Diagnostics.DescribeEntity(city)
                    + " payloadLength=" + (payload != null ? payload.Length : 0).ToString()
                    + " chunkCount=" + (chunks != null ? chunks.Count : 0).ToString()
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
                persisted = Workbenches.Json.Read<PassengerFlowPersistentState>(payload);
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
                serviceDayKey = state.ServiceDayKey,
                lastMinute = state.LastMinute,
                currentAbsoluteBucketIndex = SamplingSystem.AbsoluteBucketIndex(state.CurrentBucket),
                currentBucketServiceDayKey = state.CurrentBucket.ServiceDayKey,
                currentBucketStartMinute = state.CurrentBucket.BucketStartMinute,
                stationCatalog = state.Anchors.ExportCatalog(port),
                stationVolumes = state.Aggregates.ExportStationVolumes(),
                sectionVolumes = state.Aggregates.ExportSectionVolumes(),
                odFlows = state.Aggregates.ExportOdFlows(),
                warnings = state.Aggregates.ExportWarnings(),
                legacyStationVolumes = state.LegacyStationVolumes,
                legacySectionVolumes = state.LegacySectionVolumes,
                legacyOdFlows = state.LegacyOdFlows,
                legacyWarnings = state.LegacyWarnings
            };
        }

        internal static string Restore(PassengerFlowPersistentState persisted)
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return "runtimeStateNull";

            if (TryGetSupportFailureReason(persisted, out string failureReason))
                return failureReason;

            persisted = Migrate(persisted);
            state.Clear();

            int currentAbsoluteBucket = ResolveCurrentAbsoluteBucket(persisted);
            int minAbsoluteBucket = Math.Max(0, currentAbsoluteBucket - (BucketsPerWindow - 1));
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

            state.ServiceDayKey = persisted.serviceDayKey;
            state.LastMinute = persisted.lastMinute;
            state.CurrentBucket = new TimeBucketKey(
                persisted.currentBucketServiceDayKey,
                persisted.currentBucketStartMinute);
            state.CurrentAbsoluteBucketIndex = currentAbsoluteBucket;
            state.Anchors.RestoreCatalog(trimmed.stationCatalog);
            state.Aggregates.Restore(
                trimmed.stationVolumes,
                trimmed.sectionVolumes,
                trimmed.odFlows,
                trimmed.warnings);
            state.LegacyStationVolumes = persisted.legacyStationVolumes ?? Array.Empty<PassengerFlowPersistedStationVolume>();
            state.LegacySectionVolumes = persisted.legacySectionVolumes ?? Array.Empty<PassengerFlowPersistedSectionVolume>();
            state.LegacyOdFlows = persisted.legacyOdFlows ?? Array.Empty<PassengerFlowPersistedOdFlow>();
            state.LegacyWarnings = persisted.legacyWarnings ?? Array.Empty<PassengerFlowPersistedWarning>();

            for (int absoluteBucket = minAbsoluteBucket; absoluteBucket <= currentAbsoluteBucket; absoluteBucket++)
                state.RollingWindow.Add(SamplingSystem.BucketFromAbsoluteIndex(absoluteBucket));

            SamplingSystem.TrimRollingWindowForRestore(state);
            return string.Empty;
        }

        private static int ResolveCurrentAbsoluteBucket(PassengerFlowPersistentState persisted)
        {
            if (persisted == null)
                return 0;

            if (persisted.currentAbsoluteBucketIndex > 0)
                return persisted.currentAbsoluteBucketIndex;

            return SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(
                persisted.currentBucketServiceDayKey,
                persisted.currentBucketStartMinute));
        }

        private static bool IsSupported(PassengerFlowPersistentState persisted)
        {
            return !TryGetSupportFailureReason(persisted, out _);
        }

        private static PassengerFlowPersistentState Migrate(PassengerFlowPersistentState persisted)
        {
            if (persisted.schemaVersion == SchemaVersion)
                return persisted;

            Port port = Runtime.Current;
            DateTime nowDate = port != null ? port.NowDate() : DateTime.Today;
            int currentLegacyDayIndex = persisted.currentBucketServiceDayIndex;
            bool legacyClockCompatible = port == null
                || Math.Abs(port.FramesPerMinute() - DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE) < 0.001d;
            PassengerFlowPersistentState migrated = new PassengerFlowPersistentState
            {
                schemaVersion = SchemaVersion,
                bucketMinutes = persisted.bucketMinutes,
                serviceDayKey = SamplingSystem.ServiceDayKey(nowDate),
                lastMinute = persisted.lastDayMinute,
                currentBucketServiceDayKey = SamplingSystem.ServiceDayKey(nowDate),
                currentBucketStartMinute = persisted.currentBucketStartMinute,
                stationCatalog = persisted.stationCatalog ?? Array.Empty<PassengerFlowPersistedStationCatalog>(),
                stationVolumes = legacyClockCompatible ? MigrateRows(persisted.stationVolumes, currentLegacyDayIndex, nowDate) : Array.Empty<PassengerFlowPersistedStationVolume>(),
                sectionVolumes = legacyClockCompatible ? MigrateRows(persisted.sectionVolumes, currentLegacyDayIndex, nowDate) : Array.Empty<PassengerFlowPersistedSectionVolume>(),
                odFlows = legacyClockCompatible ? MigrateRows(persisted.odFlows, currentLegacyDayIndex, nowDate) : Array.Empty<PassengerFlowPersistedOdFlow>(),
                warnings = legacyClockCompatible
                    ? MigrateRows(persisted.warnings, currentLegacyDayIndex, nowDate)
                    : new[]
                    {
                        new PassengerFlowPersistedWarning
                        {
                            mode = TransitModeCodec.Format(TransitMode.Train),
                            code = "legacy-clock-buckets-excluded",
                            lineId = string.Empty,
                            stationSakIndex = -1,
                            serviceDayKey = SamplingSystem.ServiceDayKey(nowDate),
                            bucketStartMinute = persisted.currentBucketStartMinute,
                            count = 1
                        }
                    },
                legacyStationVolumes = legacyClockCompatible ? Array.Empty<PassengerFlowPersistedStationVolume>() : persisted.stationVolumes ?? Array.Empty<PassengerFlowPersistedStationVolume>(),
                legacySectionVolumes = legacyClockCompatible ? Array.Empty<PassengerFlowPersistedSectionVolume>() : persisted.sectionVolumes ?? Array.Empty<PassengerFlowPersistedSectionVolume>(),
                legacyOdFlows = legacyClockCompatible ? Array.Empty<PassengerFlowPersistedOdFlow>() : persisted.odFlows ?? Array.Empty<PassengerFlowPersistedOdFlow>(),
                legacyWarnings = legacyClockCompatible ? Array.Empty<PassengerFlowPersistedWarning>() : persisted.warnings ?? Array.Empty<PassengerFlowPersistedWarning>()
            };
            migrated.currentAbsoluteBucketIndex = SamplingSystem.AbsoluteBucketIndex(
                new TimeBucketKey(migrated.currentBucketServiceDayKey, migrated.currentBucketStartMinute));
            if (!legacyClockCompatible)
            {
                Diagnostics.Log("PassengerFlowLegacyClock", "warning=legacy-clock-buckets-excluded schemaVersion=1");
            }
            return migrated;
        }

        private static T[] MigrateRows<T>(T[] rows, int currentLegacyDayIndex, DateTime nowDate)
            where T : PassengerFlowPersistedBucketRow
        {
            T[] safeRows = rows ?? Array.Empty<T>();
            for (int rowIndex = 0; rowIndex < safeRows.Length; rowIndex++)
            {
                T row = safeRows[rowIndex];
                if (row == null)
                    continue;
                DateTime rowDate = nowDate.AddDays(row.serviceDayIndex - currentLegacyDayIndex);
                row.serviceDayKey = SamplingSystem.ServiceDayKey(rowDate);
            }
            return safeRows;
        }

        private static bool TryServiceDayDate(int serviceDayKey, out DateTime serviceDayDate)
        {
            try
            {
                serviceDayDate = SamplingSystem.DateFromServiceDayKey(serviceDayKey);
                return true;
            }
            catch
            {
                serviceDayDate = default;
                return false;
            }
        }

        private static bool TryGetSupportFailureReason(PassengerFlowPersistentState persisted, out string failureReason)
        {
            failureReason = string.Empty;
            if (persisted == null)
            {
                failureReason = "persistedNull";
                return true;
            }

            if (persisted.schemaVersion != 1 && persisted.schemaVersion != SchemaVersion)
            {
                failureReason = "schemaVersionMismatch(expected=" + SchemaVersion.ToString()
                    + ",actual=" + persisted.schemaVersion.ToString() + ")";
                return true;
            }

            if (persisted.bucketMinutes != Snapshot.BucketMinutes)
            {
                failureReason = "bucketMinutesMismatch(expected=" + Snapshot.BucketMinutes.ToString()
                    + ",actual=" + persisted.bucketMinutes.ToString() + ")";
                return true;
            }

            int persistedMinute = persisted.schemaVersion == 1 ? persisted.lastDayMinute : persisted.lastMinute;
            if (persistedMinute < -1 || persistedMinute >= 1440)
            {
                failureReason = "lastMinuteInvalid(" + persistedMinute.ToString() + ")";
                return true;
            }

            int persistedDayKey = persisted.schemaVersion == 1
                ? persisted.currentBucketServiceDayIndex
                : persisted.currentBucketServiceDayKey;
            if (!IsValidBucket(persistedDayKey, persisted.currentBucketStartMinute, persisted.schemaVersion))
            {
                failureReason = "currentBucketInvalid(day=" + persistedDayKey.ToString()
                    + ",minute=" + persisted.currentBucketStartMinute.ToString() + ")";
                return true;
            }

            int computedAbsoluteBucket = persisted.schemaVersion == 2
                ? SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(persistedDayKey, persisted.currentBucketStartMinute))
                : persisted.currentAbsoluteBucketIndex;
            if (persisted.schemaVersion == 2 && persisted.currentAbsoluteBucketIndex > 0
                && persisted.currentAbsoluteBucketIndex != computedAbsoluteBucket)
            {
                failureReason = "absoluteBucketMismatch(expected=" + computedAbsoluteBucket.ToString()
                    + ",actual=" + persisted.currentAbsoluteBucketIndex.ToString() + ")";
                return true;
            }

            return false;
        }

        private static bool IsValidBucket(int serviceDayKey, int bucketStartMinute, int schemaVersion = 2)
        {
            bool validDay = schemaVersion == 1
                ? serviceDayKey >= 0
                : TryServiceDayDate(serviceDayKey, out _);
            return validDay
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
                serviceDayKey = persisted.serviceDayKey,
                lastMinute = persisted.lastMinute,
                currentAbsoluteBucketIndex = maxAbsoluteBucket,
                currentBucketServiceDayKey = persisted.currentBucketServiceDayKey,
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
                    .ToArray(),
                legacyStationVolumes = persisted.legacyStationVolumes ?? Array.Empty<PassengerFlowPersistedStationVolume>(),
                legacySectionVolumes = persisted.legacySectionVolumes ?? Array.Empty<PassengerFlowPersistedSectionVolume>(),
                legacyOdFlows = persisted.legacyOdFlows ?? Array.Empty<PassengerFlowPersistedOdFlow>(),
                legacyWarnings = persisted.legacyWarnings ?? Array.Empty<PassengerFlowPersistedWarning>()
            };
        }

        private static bool IsWithinWindow(PassengerFlowPersistedBucketRow row, int minAbsoluteBucket, int maxAbsoluteBucket)
        {
            if (row == null)
                return false;

            if (!IsValidBucket(row.serviceDayKey, row.bucketStartMinute))
                return false;

            int absoluteBucket = SamplingSystem.AbsoluteBucketIndex(
                new TimeBucketKey(row.serviceDayKey, row.bucketStartMinute));
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

        private static string DescribePersistedState(PassengerFlowPersistentState persisted)
        {
            if (persisted == null)
                return "persistedState=null";

            return "persistedState="
                + "schemaVersion:" + persisted.schemaVersion.ToString()
                + ",bucketMinutes:" + persisted.bucketMinutes.ToString()
                + ",serviceDayKey:" + persisted.serviceDayKey.ToString()
                + ",lastMinute:" + persisted.lastMinute.ToString()
                + ",currentAbsoluteBucketIndex:" + persisted.currentAbsoluteBucketIndex.ToString()
                + ",currentBucket:" + persisted.currentBucketServiceDayKey.ToString()
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
                + "serviceDayKey:" + state.ServiceDayKey.ToString()
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
                + ",warnings:" + state.Aggregates.WarningCount.ToString()
                + ",rollingWindow:" + state.RollingWindow.Count.ToString();
        }
    }

    [DataContract]
    public sealed class PassengerFlowPersistentState
    {
        [DataMember] public int schemaVersion;
        [DataMember] public int bucketMinutes;
        [DataMember] public int serviceDayKey;
        [DataMember] public int lastMinute;
        [DataMember] public int currentBucketServiceDayKey;
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
        [DataMember] public PassengerFlowPersistedStationVolume[] legacyStationVolumes;
        [DataMember] public PassengerFlowPersistedSectionVolume[] legacySectionVolumes;
        [DataMember] public PassengerFlowPersistedOdFlow[] legacyOdFlows;
        [DataMember] public PassengerFlowPersistedWarning[] legacyWarnings;
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
        [DataMember] public int serviceDayKey;
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
