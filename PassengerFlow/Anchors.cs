using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Observation;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Anchors
    {
        private readonly Dictionary<string, int> m_IndexBySak = new Dictionary<string, int>();
        private readonly Dictionary<WaypointAnchorCacheKey, WaypointAnchorCacheEntry> m_WaypointCache = new Dictionary<WaypointAnchorCacheKey, WaypointAnchorCacheEntry>();
        private readonly List<StationKey> m_Stations = new List<StationKey>();

        internal int StationCount => m_Stations.Count;

        internal void Clear()
        {
            m_IndexBySak.Clear();
            m_WaypointCache.Clear();
            m_Stations.Clear();
        }

        internal void InvalidateLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<WaypointAnchorCacheKey> keys = new List<WaypointAnchorCacheKey>();
            foreach (KeyValuePair<WaypointAnchorCacheKey, WaypointAnchorCacheEntry> entry in m_WaypointCache)
            {
                if (entry.Key.IsLine(line))
                    keys.Add(entry.Key);
            }

            for (int i = 0; i < keys.Count; i++)
                m_WaypointCache.Remove(keys[i]);
        }

        internal bool TryRegister(StationDwellAnchor anchor, out StationKey key)
        {
            return TryRegisterSak(
                anchor.StationAnchorId,
                anchor.AnchorEntity,
                anchor.StopEntity,
                anchor.BuildingEntity,
                out key);
        }

        internal bool TryRegisterSak(string sak, Entity anchorEntity, Entity stopEntity, Entity buildingEntity, out StationKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(sak) || !RapidTransitMod.Stops.IsKey(sak))
                return false;

            if (!m_IndexBySak.TryGetValue(sak, out int index))
            {
                index = m_Stations.Count;
                m_IndexBySak[sak] = index;
                m_Stations.Add(new StationKey(index, sak, anchorEntity, stopEntity, buildingEntity, string.Empty));
            }
            else
            {
                m_Stations[index] = m_Stations[index].WithEntities(anchorEntity, stopEntity, buildingEntity);
            }

            key = m_Stations[index];
            return true;
        }

        internal bool TryForWaypoint(Port port, Entity line, int waypointIndex, out StationKey key)
        {
            key = default;
            WaypointAnchorCacheKey cacheKey = new WaypointAnchorCacheKey(line, waypointIndex);
            if (port == null || !port.TryWaypoint(line, waypointIndex, out Entity waypoint))
            {
                m_WaypointCache.Remove(cacheKey);
                return false;
            }

            bool hadCached = m_WaypointCache.TryGetValue(cacheKey, out WaypointAnchorCacheEntry cached);
            if (hadCached && cached.Waypoint == waypoint && TryGetCachedStation(cached, out key))
                return true;

            if (!port.TryDwellAnchor(line, waypointIndex, out StationDwellAnchor anchor)
                || !TryRegister(anchor, out key))
            {
                m_WaypointCache.Remove(cacheKey);
                return false;
            }

            m_WaypointCache[cacheKey] = new WaypointAnchorCacheEntry(
                waypoint,
                key.Index,
                key.Sak);
            return true;
        }

        private bool TryGetCachedStation(WaypointAnchorCacheEntry cached, out StationKey key)
        {
            key = default;
            if (cached.StationIndex < 0 || cached.StationIndex >= m_Stations.Count)
                return false;

            StationKey station = m_Stations[cached.StationIndex];
            if (!string.Equals(station.Sak, cached.Sak, StringComparison.Ordinal))
                return false;

            key = station;
            return true;
        }

        internal bool TryGetSak(int index, out string sak)
        {
            sak = string.Empty;
            if (index < 0 || index >= m_Stations.Count)
                return false;

            sak = m_Stations[index].Sak;
            return !string.IsNullOrWhiteSpace(sak);
        }

        internal StationCatalogDto[] BuildCatalog(Port port)
        {
            StationCatalogDto[] catalog = new StationCatalogDto[m_Stations.Count];
            for (int i = 0; i < m_Stations.Count; i++)
            {
                StationKey station = m_Stations[i];
                catalog[i] = new StationCatalogDto
                {
                    stationId = station.Sak,
                    stationName = ResolveStationName(port, station)
                };
            }

            return catalog;
        }

        internal PassengerFlowPersistedStationCatalog[] ExportCatalog(Port port)
        {
            PassengerFlowPersistedStationCatalog[] catalog = new PassengerFlowPersistedStationCatalog[m_Stations.Count];
            for (int i = 0; i < m_Stations.Count; i++)
            {
                StationKey station = m_Stations[i];
                catalog[i] = new PassengerFlowPersistedStationCatalog
                {
                    stationSakIndex = station.Index,
                    stationId = station.Sak,
                    stationName = ResolveStationName(port, station)
                };
            }

            return catalog;
        }

        internal void RestoreCatalog(PassengerFlowPersistedStationCatalog[] catalog)
        {
            m_IndexBySak.Clear();
            m_WaypointCache.Clear();
            m_Stations.Clear();
            if (catalog == null)
                return;

            for (int i = 0; i < catalog.Length; i++)
            {
                PassengerFlowPersistedStationCatalog station = catalog[i];
                if (station == null
                    || string.IsNullOrWhiteSpace(station.stationId)
                    || !RapidTransitMod.Stops.IsKey(station.stationId))
                {
                    continue;
                }

                int index = station.stationSakIndex >= 0 ? station.stationSakIndex : m_Stations.Count;
                while (m_Stations.Count <= index)
                {
                    m_Stations.Add(default);
                }

                m_Stations[index] = new StationKey(
                    index,
                    station.stationId,
                    Entity.Null,
                    Entity.Null,
                    Entity.Null,
                    station.stationName);
                m_IndexBySak[station.stationId] = index;
            }

        }

        private static string ResolveStationName(Port port, StationKey station)
        {
            if (port == null)
                return string.IsNullOrWhiteSpace(station.PersistedName) ? station.Sak : station.PersistedName;

            string name = port.StationName(station.StopEntity);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            name = port.Name(station.BuildingEntity);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            name = port.Name(station.AnchorEntity);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return string.IsNullOrWhiteSpace(station.PersistedName) ? station.Sak : station.PersistedName;
        }
    }

    internal readonly struct StationKey
    {
        internal readonly int Index;
        internal readonly string Sak;
        internal readonly Entity AnchorEntity;
        internal readonly Entity StopEntity;
        internal readonly Entity BuildingEntity;
        internal readonly string PersistedName;

        internal StationKey(
            int index,
            string sak,
            Entity anchorEntity,
            Entity stopEntity,
            Entity buildingEntity,
            string persistedName)
        {
            Index = index;
            Sak = sak ?? string.Empty;
            AnchorEntity = anchorEntity;
            StopEntity = stopEntity;
            BuildingEntity = buildingEntity;
            PersistedName = persistedName ?? string.Empty;
        }

        internal StationKey WithEntities(Entity anchorEntity, Entity stopEntity, Entity buildingEntity)
        {
            return new StationKey(
                Index,
                Sak,
                anchorEntity,
                stopEntity,
                buildingEntity,
                PersistedName);
        }
    }

    internal readonly struct WaypointAnchorCacheKey : IEquatable<WaypointAnchorCacheKey>
    {
        private readonly Entity m_Line;
        private readonly int m_WaypointIndex;

        internal WaypointAnchorCacheKey(Entity line, int waypointIndex)
        {
            m_Line = line;
            m_WaypointIndex = waypointIndex;
        }

        public bool Equals(WaypointAnchorCacheKey other)
        {
            return m_Line == other.m_Line
                && m_WaypointIndex == other.m_WaypointIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is WaypointAnchorCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (m_Line.GetHashCode() * 397) ^ m_WaypointIndex;
            }
        }

        internal bool IsLine(Entity line) => m_Line == line;
    }

    internal readonly struct WaypointAnchorCacheEntry
    {
        internal readonly Entity Waypoint;
        internal readonly int StationIndex;
        internal readonly string Sak;

        internal WaypointAnchorCacheEntry(
            Entity waypoint,
            int stationIndex,
            string sak)
        {
            Waypoint = waypoint;
            StationIndex = stationIndex;
            Sak = sak ?? string.Empty;
        }
    }
}
