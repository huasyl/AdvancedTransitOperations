using System;
using System.Collections.Generic;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class DispatchCache
    {
        private const int HistoryLimit = 8;
        private const float OutlierFactor = 1.5f;
        private const float FastMargin = 0.98f;
        private const float SlowBlend = 0.5f;
        private const float SLOW_STEP_MAX_FRAMES = 728f;

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<Entity, Entity> m_Depot;
        private readonly Func<Entity, string> m_DepotId;
        private readonly LineAnchorCatalog m_Catalog;

        public DispatchCache(
            ModRuntimeHostSystem runtime,
            Func<Entity, string> lineId,
            Func<Entity, Entity> depot,
            Func<Entity, string> depotId,
            LineAnchorCatalog catalog)
        {
            m_Runtime = runtime;
            m_LineId = lineId;
            m_Depot = depot;
            m_DepotId = depotId;
            m_Catalog = catalog;
        }

        public void Ensure()
        {
            if (m_Runtime.m_DispatchCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineDispatchDepotCacheElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineDispatchDepotHistoryElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineDispatchPrepHistoryElement>(city);
            MigrateLegacyLineIds(city);
            LogOrphanLineIds(city);
            m_Runtime.m_DispatchCacheBufferReady = true;
        }

        private bool LineIdMatches(FixedString128Bytes bufferLineId, string stableLineId)
        {
            if (string.IsNullOrEmpty(stableLineId) || bufferLineId.IsEmpty)
                return false;

            string rawStr = bufferLineId.ToString();
            if (string.Equals(rawStr, stableLineId, StringComparison.Ordinal))
                return true;

            LineKey bufferKey = LineIdentityService.GetKey(rawStr);
            if (bufferKey.IsEmpty || LineKey.IsStableGuidKey(bufferKey))
                return false;

            if (!LineKey.IsLegacyNumericKey(bufferKey))
                return false;

            if (m_Catalog == null || m_Catalog.IsLegacyConflict(bufferKey))
                return false;

            if (!m_Catalog.TryLegacy(bufferKey, out LineKey stable))
                return false;

            return string.Equals(LineIdentityService.GetId(stable), stableLineId, StringComparison.Ordinal);
        }

        private void LogOrphanLineIds(Entity city)
        {
            if (m_Catalog == null) return;
            HashSet<string> orphans = new HashSet<string>(StringComparer.Ordinal);

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
            {
                DynamicBuffer<LineDispatchDepotCacheElement> buf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city, true);
                for (int i = 0; i < buf.Length; i++)
                    CollectOrphanIfAny(buf[i].m_LineId, orphans);
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchDepotHistoryElement> buf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city, true);
                for (int i = 0; i < buf.Length; i++)
                    CollectOrphanIfAny(buf[i].m_LineId, orphans);
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchPrepHistoryElement> buf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city, true);
                for (int i = 0; i < buf.Length; i++)
                    CollectOrphanIfAny(buf[i].m_LineId, orphans);
            }

            if (orphans.Count > 0)
                m_Runtime.log.Info("[DispatchCache] orphan legacy lineIds preserved (not migrated): "
                    + string.Join(", ", orphans));
        }

        private void MigrateLegacyLineIds(Entity city)
        {
            if (m_Catalog == null) return;

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city)
                && m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchDepotCacheElement> cacheBuf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city);
                DynamicBuffer<LineDispatchDepotHistoryElement> historyBuf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city);

                HashSet<string> stableCachePresent = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < cacheBuf.Length; i++)
                    AddStableTarget(cacheBuf[i].m_LineId, cacheBuf[i].m_DepotId, stableCachePresent);
                HashSet<string> stableHistoryPresent = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < historyBuf.Length; i++)
                    AddStableTarget(historyBuf[i].m_LineId, historyBuf[i].m_DepotId, stableHistoryPresent);

                for (int i = 0; i < cacheBuf.Length; i++)
                {
                    if (TryMigrateEntry("dispatch-depot-cache", cacheBuf[i].m_LineId, cacheBuf[i].m_DepotId, stableCachePresent, out FixedString128Bytes stableLineId))
                    {
                        LineDispatchDepotCacheElement elem = cacheBuf[i];
                        elem.m_LineId = stableLineId;
                        cacheBuf[i] = elem;
                    }
                }
                for (int i = 0; i < historyBuf.Length; i++)
                {
                    if (TryMigrateEntry("dispatch-depot-history", historyBuf[i].m_LineId, historyBuf[i].m_DepotId, stableHistoryPresent, out FixedString128Bytes stableLineId))
                    {
                        LineDispatchDepotHistoryElement elem = historyBuf[i];
                        elem.m_LineId = stableLineId;
                        historyBuf[i] = elem;
                    }
                }
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchPrepHistoryElement> prepBuf =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city);
                HashSet<string> stablePresent = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < prepBuf.Length; i++)
                    AddStableTarget(prepBuf[i].m_LineId, prepBuf[i].m_DepotId, stablePresent);
                for (int i = 0; i < prepBuf.Length; i++)
                {
                    if (TryMigrateEntry("dispatch-prep-history", prepBuf[i].m_LineId, prepBuf[i].m_DepotId, stablePresent, out FixedString128Bytes stableLineId))
                    {
                        LineDispatchPrepHistoryElement elem = prepBuf[i];
                        elem.m_LineId = stableLineId;
                        prepBuf[i] = elem;
                    }
                }
            }
        }

        private static void AddStableTarget(
            FixedString128Bytes raw,
            FixedString128Bytes depotId,
            HashSet<string> stablePresent)
        {
            if (raw.IsEmpty) return;
            string rawStr = raw.ToString();
            if (string.IsNullOrEmpty(rawStr)) return;
            if (LineKey.IsStableGuidKey(LineIdentityService.GetKey(rawStr)))
                stablePresent.Add(CacheTarget(rawStr, depotId));
        }

        private bool TryMigrateEntry(
            string domain,
            FixedString128Bytes raw,
            FixedString128Bytes depotId,
            HashSet<string> stablePresent,
            out FixedString128Bytes stableLineId)
        {
            stableLineId = default;
            if (raw.IsEmpty) return false;
            string rawStr = raw.ToString();
            if (string.IsNullOrEmpty(rawStr)) return false;

            LineKey legacyKey = LineIdentityService.GetKey(rawStr);
            if (LineKey.IsStableGuidKey(legacyKey)) return false;
            if (!LineKey.IsLegacyNumericKey(legacyKey))
            {
                LogCacheMigration(domain, legacyKey, LineKey.Empty, depotId, "Rejected", "invalid-legacy-key");
                return false;
            }
            if (m_Catalog.IsLegacyConflict(legacyKey))
            {
                LogCacheMigration(domain, legacyKey, LineKey.Empty, depotId, "LegacyConflict", "duplicate-route-number");
                return false;
            }
            if (!m_Catalog.TryLegacy(legacyKey, out LineKey stable))
            {
                LogCacheMigration(domain, legacyKey, LineKey.Empty, depotId, "ZeroMatch", "no-live-line-match");
                return false;
            }

            string stableId = LineIdentityService.GetId(stable);
            string target = CacheTarget(stableId, depotId);
            if (stablePresent.Contains(target))
            {
                LogCacheMigration(domain, legacyKey, stable, depotId, "TargetOccupied", "stable-target-occupied");
                return false;
            }

            stablePresent.Add(target);
            stableLineId = stableId;
            LogCacheMigration(domain, legacyKey, stable, depotId, "Migrated", "ok");
            return true;
        }

        private void LogCacheMigration(
            string domain,
            LineKey legacy,
            LineKey stable,
            FixedString128Bytes depotId,
            string result,
            string reason)
        {
            m_Runtime.log.Info("[LineKeyMigration] domain=" + domain
                + " mode=" + TransitModeCodec.Format(legacy.Mode)
                + " routeNumber=" + (LineKey.IsLegacyNumericKey(legacy) ? legacy.Id : "-")
                + " newGuid=" + (LineKey.IsStableGuidKey(stable) ? stable.Id : "-")
                + " depot=" + (depotId.IsEmpty ? "-" : depotId.ToString())
                + " result=" + result
                + " reason=" + reason);
        }

        private static string CacheTarget(string lineId, FixedString128Bytes depotId)
        {
            return (lineId ?? string.Empty) + "\n" + depotId.ToString();
        }

        private void CollectOrphanIfAny(FixedString128Bytes raw, HashSet<string> orphans)
        {
            if (raw.IsEmpty) return;
            string rawStr = raw.ToString();
            if (string.IsNullOrEmpty(rawStr)) return;

            LineKey key = LineIdentityService.GetKey(rawStr);
            if (key.IsEmpty || LineKey.IsStableGuidKey(key))
                return;

            if (!LineKey.IsLegacyNumericKey(key))
            {
                orphans.Add(rawStr);
                return;
            }

            if (m_Catalog.IsLegacyConflict(key) || !m_Catalog.TryLegacy(key, out _))
                orphans.Add(rawStr);
        }

        public float Read(Entity line)
        {
            if (!m_Runtime.m_DispatchCacheBufferReady) return 0f;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            string lineId = m_LineId(line);
            Entity configuredDepot = m_Depot(line);
            string configuredDepotId = m_DepotId(configuredDepot);
            return ReadDepot(city, lineId, configuredDepotId);
        }

        public void Update(Entity line, Entity vehicle, uint sampleFrames)
        {
            if (!m_Runtime.m_DispatchCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            UpdateDepot(city, line, vehicle, sampleFrames);
        }

        public uint ReadPrep(Entity line)
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (!m_Runtime.m_DispatchCacheBufferReady || city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city)) return 360u;
            string lineId = m_LineId(line);
            FixedString128Bytes depotId = m_DepotId(m_Depot(line));
            DynamicBuffer<LineDispatchPrepHistoryElement> buffer = m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city, true);
            int index = FindPrep(buffer, lineId, depotId);
            if (index >= 0)
            {
                LineDispatchPrepHistoryElement value = buffer[index];
                uint maximum = 0u;
                List<uint> samples = ReadPrepSamples(value);
                for (int sample = 0; sample < samples.Count; sample++) maximum = math.max(maximum, samples[sample]);
                return maximum > 0u ? maximum : 360u;
            }
            return 360u;
        }

        public void RecordPrep(Entity line, uint rawFrames)
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (!m_Runtime.m_DispatchCacheBufferReady || city == Entity.Null || line == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city)) return;
            string lineIdStr = m_LineId(line);
            FixedString128Bytes lineId = lineIdStr;
            FixedString128Bytes depotId = m_DepotId(m_Depot(line));
            if (lineId.IsEmpty || depotId.IsEmpty) return;
            uint saved = math.min(rawFrames, 360u);
            DynamicBuffer<LineDispatchPrepHistoryElement> buffer = m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city);
            int index = FindPrep(buffer, lineIdStr, depotId);
            if (index >= 0)
            {
                LineDispatchPrepHistoryElement value = buffer[index];
                AppendPrep(ref value, saved);
                value.m_LineId = lineId;
                buffer[index] = value;
                LogPrep(line, rawFrames, saved, value.m_SampleCount);
                return;
            }
            LineDispatchPrepHistoryElement created = new LineDispatchPrepHistoryElement { m_LineId = lineId, m_DepotId = depotId };
            AppendPrep(ref created, saved);
            buffer.Add(created);
            LogPrep(line, rawFrames, saved, created.m_SampleCount);
        }

        private float ReadDepot(Entity city, string lineId, string depotId)
        {
            if (city == Entity.Null || string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(depotId))
                return 0f;
            if (!m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
                return 0f;

            FixedString128Bytes depotKey = depotId;
            DynamicBuffer<LineDispatchDepotCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city, true);
            int index = FindDepot(buf, lineId, depotKey);
            return index >= 0 ? buf[index].m_DepotToOriginFrames : 0f;
        }

        private bool UpdateDepot(Entity city, Entity line, Entity vehicle, uint sampleFrames)
        {
            if (city == Entity.Null
                || line == Entity.Null
                || vehicle == Entity.Null
                || sampleFrames == 0
                || !m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                return false;
            }

            string lineId = m_LineId(line);
            Entity configuredDepot = m_Depot(line);
            string depotId = m_DepotId(configuredDepot);
            if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(depotId))
                return false;

            FixedString128Bytes lineKey = lineId;
            FixedString128Bytes depotKey = depotId;
            DynamicBuffer<LineDispatchDepotCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city);
            DynamicBuffer<LineDispatchDepotHistoryElement> historyBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city);
            int index = FindDepot(buf, lineId, depotKey);
            if (index >= 0)
            {
                uint oldFrames = buf[index].m_DepotToOriginFrames;
                FixedString128Bytes legacyLineId = buf[index].m_LineId;
                FixedString128Bytes historyLineId = HasDepotHistory(historyBuf, lineKey, depotKey)
                    ? lineKey
                    : legacyLineId;
                LineDispatchDepotHistoryElement history = GetDepotHistory(historyBuf, historyLineId, depotKey);
                history.m_LineId = lineKey;
                LineDispatchDepotHistoryElement updatedHistory = AppendDepot(history, sampleFrames);
                uint newFrames = Adaptive(oldFrames, sampleFrames);
                buf[index] = new LineDispatchDepotCacheElement
                {
                    m_LineId = lineKey,
                    m_DepotId = depotKey,
                    m_DepotToOriginFrames = newFrames
                };
                if (!historyLineId.Equals(lineKey))
                    RemoveDepotHistory(historyBuf, legacyLineId, depotKey);
                UpsertDepot(historyBuf, updatedHistory);
                LogDepot(line, depotId, sampleFrames, oldFrames, newFrames, updatedHistory.m_SampleCount);
                return true;
            }

            LineDispatchDepotHistoryElement createdHistory = AppendDepot(new LineDispatchDepotHistoryElement
            {
                m_LineId = lineKey,
                m_DepotId = depotKey
            }, sampleFrames);
            buf.Add(new LineDispatchDepotCacheElement
            {
                m_LineId = lineKey,
                m_DepotId = depotKey,
                m_DepotToOriginFrames = sampleFrames
            });
            UpsertDepot(historyBuf, createdHistory);
            LogDepot(line, depotId, sampleFrames, 0, sampleFrames, createdHistory.m_SampleCount);
            return true;
        }

        private int FindDepot(
            DynamicBuffer<LineDispatchDepotCacheElement> buffer,
            string lineId,
            FixedString128Bytes depotId)
        {
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_LineId.Equals((FixedString128Bytes)lineId) && buffer[i].m_DepotId == depotId)
                    return i;
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_DepotId == depotId && LineIdMatches(buffer[i].m_LineId, lineId))
                    return i;
            return -1;
        }

        private int FindPrep(
            DynamicBuffer<LineDispatchPrepHistoryElement> buffer,
            string lineId,
            FixedString128Bytes depotId)
        {
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_LineId.Equals((FixedString128Bytes)lineId) && buffer[i].m_DepotId == depotId)
                    return i;
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_DepotId == depotId && LineIdMatches(buffer[i].m_LineId, lineId))
                    return i;
            return -1;
        }

        private static bool HasDepotHistory(
            DynamicBuffer<LineDispatchDepotHistoryElement> buffer,
            FixedString128Bytes lineId,
            FixedString128Bytes depotId)
        {
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_LineId == lineId && buffer[i].m_DepotId == depotId)
                    return true;
            return false;
        }

        public void RemoveLine(Entity line)
        {
            if (line == Entity.Null) return;
            if (!m_Runtime.m_DispatchCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchCacheElement>(city))
            {
                DynamicBuffer<LineDispatchCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineDispatchCacheElement>(city);
                for (int i = buf.Length - 1; i >= 0; i--)
                {
                    if (buf[i].m_LineEntity == line)
                        buf.RemoveAt(i);
                }
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchHistoryElement> historyBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchHistoryElement>(city);
                for (int i = historyBuf.Length - 1; i >= 0; i--)
                {
                    if (historyBuf[i].m_LineEntity == line)
                        historyBuf.RemoveAt(i);
                }
            }

            string lineId = m_LineId(line);
            if (string.IsNullOrEmpty(lineId))
                return;

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
            {
                DynamicBuffer<LineDispatchDepotCacheElement> depotBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city);
                for (int i = depotBuf.Length - 1; i >= 0; i--)
                {
                    if (LineIdMatches(depotBuf[i].m_LineId, lineId))
                        depotBuf.RemoveAt(i);
                }
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchDepotHistoryElement> depotHistoryBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city);
                for (int i = depotHistoryBuf.Length - 1; i >= 0; i--)
                {
                    if (LineIdMatches(depotHistoryBuf[i].m_LineId, lineId))
                        depotHistoryBuf.RemoveAt(i);
                }
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchPrepHistoryElement> prep = m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city);
                for (int i = prep.Length - 1; i >= 0; i--)
                    if (LineIdMatches(prep[i].m_LineId, lineId)) prep.RemoveAt(i);
            }
        }

        public void RemoveDepotTiming(Entity line)
        {
            if (line == Entity.Null || !m_Runtime.m_DispatchCacheBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            string lineId = m_LineId(line);
            if (city == Entity.Null || string.IsNullOrEmpty(lineId))
                return;

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
            {
                DynamicBuffer<LineDispatchDepotCacheElement> cache =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city);
                for (int i = cache.Length - 1; i >= 0; i--)
                    if (LineIdMatches(cache[i].m_LineId, lineId)) cache.RemoveAt(i);
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchDepotHistoryElement> history =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city);
                for (int i = history.Length - 1; i >= 0; i--)
                    if (LineIdMatches(history[i].m_LineId, lineId)) history.RemoveAt(i);
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchPrepHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchPrepHistoryElement> prep =
                    m_Runtime.EntityManager.GetBuffer<LineDispatchPrepHistoryElement>(city);
                for (int i = prep.Length - 1; i >= 0; i--)
                    if (LineIdMatches(prep[i].m_LineId, lineId)) prep.RemoveAt(i);
            }
        }

        private static List<uint> ReadPrepSamples(LineDispatchPrepHistoryElement value)
        {
            var samples = new List<uint>(HistoryLimit);
            Add(samples, value.m_Sample0); Add(samples, value.m_Sample1);
            Add(samples, value.m_Sample2); Add(samples, value.m_Sample3);
            Add(samples, value.m_Sample4); Add(samples, value.m_Sample5);
            Add(samples, value.m_Sample6); Add(samples, value.m_Sample7);
            if (samples.Count > value.m_SampleCount)
                samples.RemoveRange(value.m_SampleCount, samples.Count - value.m_SampleCount);
            return samples;
        }

        private static void AppendPrep(ref LineDispatchPrepHistoryElement value, uint sample)
        {
            List<uint> samples = ReadPrepSamples(value);
            samples.Add(sample);
            if (samples.Count > HistoryLimit) samples.RemoveAt(0);
            value.m_SampleCount = (byte)samples.Count;
            value.m_Sample0 = samples.Count > 0 ? samples[0] : 0u;
            value.m_Sample1 = samples.Count > 1 ? samples[1] : 0u;
            value.m_Sample2 = samples.Count > 2 ? samples[2] : 0u;
            value.m_Sample3 = samples.Count > 3 ? samples[3] : 0u;
            value.m_Sample4 = samples.Count > 4 ? samples[4] : 0u;
            value.m_Sample5 = samples.Count > 5 ? samples[5] : 0u;
            value.m_Sample6 = samples.Count > 6 ? samples[6] : 0u;
            value.m_Sample7 = samples.Count > 7 ? samples[7] : 0u;
        }

        private void LogPrep(Entity line, uint raw, uint saved, byte count)
        {
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[SpawnLeadPrep] line=" + line.Index + " rawFrames=" + raw
                    + " usedFrames=" + saved + " samples=" + count);
            }
        }

        private static LineDispatchDepotHistoryElement GetDepotHistory(
            DynamicBuffer<LineDispatchDepotHistoryElement> historyBuf,
            FixedString128Bytes lineId,
            FixedString128Bytes depotId)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineId == lineId && historyBuf[i].m_DepotId == depotId)
                    return historyBuf[i];
            }
            return new LineDispatchDepotHistoryElement
            {
                m_LineId = lineId,
                m_DepotId = depotId
            };
        }

        private static void UpsertDepot(
            DynamicBuffer<LineDispatchDepotHistoryElement> historyBuf,
            LineDispatchDepotHistoryElement history)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineId != history.m_LineId || historyBuf[i].m_DepotId != history.m_DepotId)
                    continue;
                historyBuf[i] = history;
                return;
            }
            historyBuf.Add(history);
        }

        private static void RemoveDepotHistory(
            DynamicBuffer<LineDispatchDepotHistoryElement> historyBuf,
            FixedString128Bytes lineId,
            FixedString128Bytes depotId)
        {
            for (int i = historyBuf.Length - 1; i >= 0; i--)
            {
                if (historyBuf[i].m_LineId == lineId && historyBuf[i].m_DepotId == depotId)
                    historyBuf.RemoveAt(i);
            }
        }

        private static LineDispatchDepotHistoryElement AppendDepot(
            LineDispatchDepotHistoryElement element,
            uint sampleFrames)
        {
            List<uint> samples = ReadDepotSamples(element);
            samples.Add(sampleFrames);
            if (samples.Count > HistoryLimit)
                samples.RemoveAt(0);

            WriteDepotSamples(ref element, samples);
            return element;
        }

        private static List<uint> ReadDepotSamples(LineDispatchDepotHistoryElement element)
        {
            List<uint> samples = new List<uint>(HistoryLimit);
            Add(samples, element.m_Sample0);
            Add(samples, element.m_Sample1);
            Add(samples, element.m_Sample2);
            Add(samples, element.m_Sample3);
            Add(samples, element.m_Sample4);
            Add(samples, element.m_Sample5);
            Add(samples, element.m_Sample6);
            Add(samples, element.m_Sample7);
            if (samples.Count > element.m_SampleCount)
                samples.RemoveRange((int)element.m_SampleCount, samples.Count - (int)element.m_SampleCount);
            return samples;
        }

        private static void WriteDepotSamples(ref LineDispatchDepotHistoryElement element, List<uint> samples)
        {
            element.m_SampleCount = (byte)math.min(samples.Count, HistoryLimit);
            element.m_Sample0 = samples.Count > 0 ? samples[0] : 0;
            element.m_Sample1 = samples.Count > 1 ? samples[1] : 0;
            element.m_Sample2 = samples.Count > 2 ? samples[2] : 0;
            element.m_Sample3 = samples.Count > 3 ? samples[3] : 0;
            element.m_Sample4 = samples.Count > 4 ? samples[4] : 0;
            element.m_Sample5 = samples.Count > 5 ? samples[5] : 0;
            element.m_Sample6 = samples.Count > 6 ? samples[6] : 0;
            element.m_Sample7 = samples.Count > 7 ? samples[7] : 0;
        }

        private static uint Adaptive(uint oldFrames, uint sampleFrames)
        {
            if (oldFrames == 0)
                return sampleFrames;

            float oldValue = oldFrames;
            float sampleValue = sampleFrames;
            if (sampleValue <= oldValue * FastMargin)
                return sampleFrames;

            if (sampleValue <= oldValue)
                return (uint)math.round(sampleValue);

            float maxStepFrames = SLOW_STEP_MAX_FRAMES;
            float blended = oldValue + (sampleValue - oldValue) * SlowBlend;
            float capped = math.min(blended, oldValue + maxStepFrames);
            return (uint)math.round(capped);
        }

        private void LogDepot(
            Entity line,
            string depotId,
            uint sampleFrames,
            uint oldFrames,
            uint newFrames,
            byte sampleCount)
        {
            string mode = oldFrames == 0
                ? "new"
                : newFrames < oldFrames
                    ? "fast-down"
                    : newFrames > oldFrames
                        ? "slow-up"
                        : "hold";
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[出库缓存] 线路" + line.Index
                    + " depot=" + depotId
                    + " 样本=" + m_Runtime.m_SimClock.ToMinutes(sampleFrames).ToString("F1") + "分钟"
                    + " 最近" + sampleCount + "条"
                    + " ETA=" + m_Runtime.m_SimClock.ToMinutes(newFrames).ToString("F1") + "分钟"
                    + (oldFrames > 0 ? " 旧值" + m_Runtime.m_SimClock.ToMinutes(oldFrames).ToString("F1") + "分钟" : "")
                    + " mode=" + mode);
            }
        }

        private static LineDispatchHistoryElement GetHistory(DynamicBuffer<LineDispatchHistoryElement> historyBuf, Entity line)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity == line)
                    return historyBuf[i];
            }
            return new LineDispatchHistoryElement
            {
                m_LineEntity = line
            };
        }

        private static void Upsert(DynamicBuffer<LineDispatchHistoryElement> historyBuf, LineDispatchHistoryElement history)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity != history.m_LineEntity) continue;
                historyBuf[i] = history;
                return;
            }
            historyBuf.Add(history);
        }

        private static LineDispatchHistoryElement Append(LineDispatchHistoryElement element, uint sampleFrames)
        {
            List<uint> samples = ReadSamples(element);
            samples.Add(sampleFrames);
            if (samples.Count > HistoryLimit)
                samples.RemoveAt(0);

            WriteSamples(ref element, samples);
            return element;
        }

        private static List<uint> ReadSamples(LineDispatchHistoryElement element)
        {
            List<uint> samples = new List<uint>(HistoryLimit);
            Add(samples, element.m_Sample0);
            Add(samples, element.m_Sample1);
            Add(samples, element.m_Sample2);
            Add(samples, element.m_Sample3);
            Add(samples, element.m_Sample4);
            Add(samples, element.m_Sample5);
            Add(samples, element.m_Sample6);
            Add(samples, element.m_Sample7);
            if (samples.Count > element.m_SampleCount)
                samples.RemoveRange((int)element.m_SampleCount, samples.Count - (int)element.m_SampleCount);
            return samples;
        }

        private static void Add(List<uint> samples, uint value)
        {
            if (value > 0)
                samples.Add(value);
        }

        private static void WriteSamples(ref LineDispatchHistoryElement element, List<uint> samples)
        {
            element.m_SampleCount = (byte)math.min(samples.Count, HistoryLimit);
            element.m_Sample0 = samples.Count > 0 ? samples[0] : 0;
            element.m_Sample1 = samples.Count > 1 ? samples[1] : 0;
            element.m_Sample2 = samples.Count > 2 ? samples[2] : 0;
            element.m_Sample3 = samples.Count > 3 ? samples[3] : 0;
            element.m_Sample4 = samples.Count > 4 ? samples[4] : 0;
            element.m_Sample5 = samples.Count > 5 ? samples[5] : 0;
            element.m_Sample6 = samples.Count > 6 ? samples[6] : 0;
            element.m_Sample7 = samples.Count > 7 ? samples[7] : 0;
        }

        private static uint Average(List<uint> samples)
        {
            if (samples.Count == 0)
                return 0;

            double sum = 0d;
            for (int i = 0; i < samples.Count; i++)
                sum += samples[i];
            double mean = sum / samples.Count;
            double maxAccepted = mean * OutlierFactor;

            double filteredSum = 0d;
            int filteredCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] > maxAccepted)
                    continue;
                filteredSum += samples[i];
                filteredCount++;
            }

            if (filteredCount == 0)
                return (uint)math.round((float)mean);

            return (uint)math.round((float)(filteredSum / filteredCount));
        }
    }
}
