using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class LapCache
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public LapCache(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Ensure()
        {
            if (m_Runtime.m_LapCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<LineLapCacheElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineLapCacheElement>(city);
            m_Runtime.m_LapCacheBufferReady = true;
        }

        public void Flush(Entity line)
        {
            if (!m_Runtime.m_LapCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<LineLapCacheElement>(city)) return;

            uint bestFrames = 0;
            float bestDist = 0f;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v0 = rvs[i].m_Vehicle;
                    if (!m_Runtime.EntityManager.Exists(v0)) continue;
                    if (m_Runtime.m_ObsPersist.TryLapFrames(v0, out uint lf) && lf > bestFrames)
                    {
                        bestFrames = lf;
                        m_Runtime.m_ObsPersist.TryLapDistance(v0, out bestDist);
                    }
                }
            }
            if (bestFrames == 0) return;

            DynamicBuffer<LineLapCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineLapCacheElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                {
                    if (bestFrames > buf[i].m_MaxLapFrames)
                    {
                        buf[i] = new LineLapCacheElement
                        {
                            m_LineEntity = line,
                            m_MaxLapFrames = bestFrames,
                            m_MaxLapDistance = bestDist
                        };
                        if (RtLog.VerboseEnabled)
                        {
                            m_Runtime.log.Info("[缓存写入] 线路" + line.Index
                                + " 圈时=" + m_Runtime.m_SimClock.ToMinutes(bestFrames).ToString("F1") + "游戏分钟");
                        }
                    }
                    return;
                }
            }

            buf.Add(new LineLapCacheElement
            {
                m_LineEntity = line,
                m_MaxLapFrames = bestFrames,
                m_MaxLapDistance = bestDist
            });
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[缓存新增] 线路" + line.Index
                    + " 圈时=" + m_Runtime.m_SimClock.ToMinutes(bestFrames).ToString("F1") + "游戏分钟");
            }
        }

        public float Read(Entity line)
        {
            if (!m_Runtime.m_LapCacheBufferReady) return 0f;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!m_Runtime.EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            DynamicBuffer<LineLapCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapFrames;
            }
            return 0f;
        }

        public float Distance(Entity line)
        {
            if (!m_Runtime.m_LapCacheBufferReady) return 0f;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!m_Runtime.EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            DynamicBuffer<LineLapCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapDistance;
            }
            return 0f;
        }

        public void RemoveLine(Entity line)
        {
            if (line == Entity.Null) return;
            if (!m_Runtime.m_LapCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<LineLapCacheElement>(city)) return;

            DynamicBuffer<LineLapCacheElement> buf = m_Runtime.EntityManager.GetBuffer<LineLapCacheElement>(city);
            for (int i = buf.Length - 1; i >= 0; i--)
            {
                if (buf[i].m_LineEntity == line)
                    buf.RemoveAt(i);
            }
        }
    }
}
