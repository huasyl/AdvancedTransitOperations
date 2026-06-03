using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class DepotResolver
    {
        private const uint LogFrames = 3600u;

        private readonly EntityManager m_EntityManager;
        private readonly Func<uint> m_Frame;
        private readonly Func<string, Entity> m_DepotById;
        private readonly Func<Entity, string> m_DepotId;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<Entity, string> m_DepotCfg;
        private readonly Func<ulong> m_Version;
        private readonly Action<string> m_Log;
        private readonly Dictionary<Entity, Entry> m_Cache = new Dictionary<Entity, Entry>();
        private uint m_LastLogFrame;
        private int m_Calls;
        private int m_Hits;
        private int m_VersionMisses;
        private int m_LineMisses;
        private int m_DepotMisses;
        private int m_Invalidations;
        private int m_Fallbacks;

        private readonly struct Entry
        {
            public readonly Entity Line;
            public readonly string LineId;
            public readonly string DepotId;
            public readonly Entity Depot;
            public readonly ulong Version;

            public Entry(Entity line, string lineId, string depotId, Entity depot, ulong version)
            {
                Line = line;
                LineId = lineId ?? string.Empty;
                DepotId = depotId ?? string.Empty;
                Depot = depot;
                Version = version;
            }
        }

        internal DepotResolver(
            EntityManager entityManager,
            Func<uint> frame,
            Func<string, Entity> depotById,
            Func<Entity, string> depotId,
            Func<Entity, string> lineId,
            Func<Entity, string> depotCfg,
            Func<ulong> version,
            Action<string> log)
        {
            m_EntityManager = entityManager;
            m_Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            m_DepotById = depotById ?? throw new ArgumentNullException(nameof(depotById));
            m_DepotId = depotId ?? throw new ArgumentNullException(nameof(depotId));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_DepotCfg = depotCfg ?? throw new ArgumentNullException(nameof(depotCfg));
            m_Version = version ?? throw new ArgumentNullException(nameof(version));
            m_Log = log;
        }

        internal void Clear()
        {
            m_Cache.Clear();
            m_Calls = 0;
            m_Hits = 0;
            m_VersionMisses = 0;
            m_LineMisses = 0;
            m_DepotMisses = 0;
            m_Invalidations = 0;
            m_Fallbacks = 0;
            m_LastLogFrame = 0;
        }

        internal string NormId(string depotId)
        {
            if (string.IsNullOrWhiteSpace(depotId))
                return string.Empty;

            Entity depot = Canon(m_DepotById(depotId));
            return depot != Entity.Null ? m_DepotId(depot) : string.Empty;
        }

        internal Entity Get(Entity line)
        {
            uint nowFrame = m_Frame();
            LogStats(nowFrame);
            m_Calls++;

            if (line == Entity.Null || !m_EntityManager.Exists(line))
            {
                m_Cache.Remove(line);
                return Entity.Null;
            }

            string lineId = m_LineId(line) ?? string.Empty;
            string depotId = m_DepotCfg(line) ?? string.Empty;
            ulong version = m_Version();

            if (m_Cache.TryGetValue(line, out Entry cached) && cached.Line == line)
            {
                if (cached.Version != version)
                {
                    m_VersionMisses++;
                    m_Cache.Remove(line);
                }
                else if (!string.Equals(cached.LineId, lineId, StringComparison.Ordinal))
                {
                    m_LineMisses++;
                    m_Cache.Remove(line);
                }
                else if (!string.Equals(cached.DepotId, depotId, StringComparison.Ordinal))
                {
                    m_DepotMisses++;
                    m_Cache.Remove(line);
                }
                else
                {
                    if (cached.Depot == Entity.Null)
                    {
                        m_Hits++;
                        return Entity.Null;
                    }

                    if (m_EntityManager.Exists(cached.Depot)
                        && m_EntityManager.HasComponent<TransportDepot>(cached.Depot)
                        && !m_EntityManager.HasComponent<Deleted>(cached.Depot))
                    {
                        m_Hits++;
                        return cached.Depot;
                    }

                    m_Invalidations++;
                    m_Cache.Remove(line);
                }
            }

            if (string.IsNullOrEmpty(depotId))
            {
                m_Cache[line] = new Entry(line, lineId, string.Empty, Entity.Null, version);
                return Entity.Null;
            }

            m_Fallbacks++;
            Entity resolved = Canon(m_DepotById(depotId));
            if (resolved != Entity.Null)
            {
                m_Cache[line] = new Entry(line, lineId, depotId, resolved, version);
            }
            else
            {
                m_Cache.Remove(line);
            }

            return resolved;
        }

        internal Entity Canon(Entity depot)
        {
            if (depot == Entity.Null || !m_EntityManager.Exists(depot))
                return Entity.Null;

            Entity current = depot;
            Entity canonical = Entity.Null;
            for (int i = 0; i < 16 && current != Entity.Null && m_EntityManager.Exists(current); i++)
            {
                if (m_EntityManager.HasComponent<TransportDepot>(current))
                {
                    canonical = current;
                }

                if (!m_EntityManager.HasComponent<Owner>(current))
                    break;

                Entity next = m_EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (next == current)
                    break;

                current = next;
            }

            return canonical != Entity.Null && m_EntityManager.Exists(canonical)
                ? canonical
                : Entity.Null;
        }

        private void LogStats(uint nowFrame)
        {
            if (m_LastLogFrame != 0 && (nowFrame - m_LastLogFrame) < LogFrames)
                return;

            if (m_Calls > 0)
            {
                m_Log?.Invoke(
                    "[ConfiguredDepotCache] intervalFrames=" + LogFrames
                    + " calls=" + m_Calls
                    + " hits=" + m_Hits
                    + " settingsMiss=" + m_VersionMisses
                    + " lineIdMiss=" + m_LineMisses
                    + " depotIdMiss=" + m_DepotMisses
                    + " entityInvalid=" + m_Invalidations
                    + " fallbackResolve=" + m_Fallbacks);
            }

            m_LastLogFrame = nowFrame;
            m_Calls = 0;
            m_Hits = 0;
            m_VersionMisses = 0;
            m_LineMisses = 0;
            m_DepotMisses = 0;
            m_Invalidations = 0;
            m_Fallbacks = 0;
        }
    }
}
