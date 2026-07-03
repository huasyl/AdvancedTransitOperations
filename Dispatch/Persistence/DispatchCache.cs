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
        private const float SlowStepMin = 4f;

        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<Entity, Entity> m_Depot;
        private readonly Func<Entity, string> m_DepotId;

        public DispatchCache(
            DispatchRuntimeSystem runtime,
            Func<Entity, string> lineId,
            Func<Entity, Entity> depot,
            Func<Entity, string> depotId)
        {
            m_Runtime = runtime;
            m_LineId = lineId;
            m_Depot = depot;
            m_DepotId = depotId;
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
            m_Runtime.m_DispatchCacheBufferReady = true;
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

        private float ReadDepot(Entity city, string lineId, string depotId)
        {
            if (city == Entity.Null || string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(depotId))
                return 0f;
            if (!m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
                return 0f;

            FixedString128Bytes lineKey = lineId;
            FixedString128Bytes depotKey = depotId;
            DynamicBuffer<LineDispatchDepotCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineId == lineKey && buf[i].m_DepotId == depotKey)
                    return buf[i].m_DepotToOriginFrames;
            }
            return 0f;
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
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineId != lineKey || buf[i].m_DepotId != depotKey)
                    continue;

                uint oldFrames = buf[i].m_DepotToOriginFrames;
                LineDispatchDepotHistoryElement history = GetDepotHistory(historyBuf, lineKey, depotKey);
                LineDispatchDepotHistoryElement updatedHistory = AppendDepot(history, sampleFrames);
                uint newFrames = Adaptive(oldFrames, sampleFrames);
                buf[i] = new LineDispatchDepotCacheElement
                {
                    m_LineId = lineKey,
                    m_DepotId = depotKey,
                    m_DepotToOriginFrames = newFrames
                };
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

            FixedString128Bytes lineKey = lineId;
            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotCacheElement>(city))
            {
                DynamicBuffer<LineDispatchDepotCacheElement> depotBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotCacheElement>(city);
                for (int i = depotBuf.Length - 1; i >= 0; i--)
                {
                    if (depotBuf[i].m_LineId == lineKey)
                        depotBuf.RemoveAt(i);
                }
            }

            if (m_Runtime.EntityManager.HasBuffer<LineDispatchDepotHistoryElement>(city))
            {
                DynamicBuffer<LineDispatchDepotHistoryElement> depotHistoryBuf = m_Runtime.EntityManager.GetBuffer<LineDispatchDepotHistoryElement>(city);
                for (int i = depotHistoryBuf.Length - 1; i >= 0; i--)
                {
                    if (depotHistoryBuf[i].m_LineId == lineKey)
                        depotHistoryBuf.RemoveAt(i);
                }
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

            float maxStepFrames = SlowStepMin * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
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
                    + " 样本=" + (sampleFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                    + " 最近" + sampleCount + "条"
                    + " ETA=" + (newFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                    + (oldFrames > 0 ? " 旧值" + (oldFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟" : "")
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
