using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Catalog
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<EntityQuery> m_LineQuery;
        private readonly Func<EntityQuery> m_DepotQuery;
        private readonly Func<Entity, string> m_Name;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, Entity> m_Anchor;
        private readonly Func<Entity, string> m_Sak;
        private readonly Func<Entity, string> m_StationName;
        private readonly Func<Entity, LineKey> m_LineKey;
        private readonly Func<LineKey, string> m_LineId;
        private readonly Func<Entity, string> m_TransportType;
        private readonly Func<Entity, Entity> m_DepotCanon;

        internal Catalog(
            EntityManager entityManager,
            Func<EntityQuery> lineQuery,
            Func<EntityQuery> depotQuery,
            Func<Entity, string> name,
            Func<Entity, Entity> stop,
            Func<Entity, Entity> anchor,
            Func<Entity, string> sak,
            Func<Entity, string> stationName,
            Func<Entity, LineKey> lineKey,
            Func<LineKey, string> lineId,
            Func<Entity, string> transportType,
            Func<Entity, Entity> depotCanon)
        {
            m_EntityManager = entityManager;
            m_LineQuery = lineQuery ?? throw new ArgumentNullException(nameof(lineQuery));
            m_DepotQuery = depotQuery ?? throw new ArgumentNullException(nameof(depotQuery));
            m_Name = name ?? throw new ArgumentNullException(nameof(name));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            m_Sak = sak ?? throw new ArgumentNullException(nameof(sak));
            m_StationName = stationName ?? throw new ArgumentNullException(nameof(stationName));
            m_LineKey = lineKey ?? throw new ArgumentNullException(nameof(lineKey));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_TransportType = transportType ?? throw new ArgumentNullException(nameof(transportType));
            m_DepotCanon = depotCanon ?? throw new ArgumentNullException(nameof(depotCanon));
        }

        internal List<WorkbenchLineRuntime> Lines()
        {
            List<WorkbenchLineRuntime> lines = new List<WorkbenchLineRuntime>();
            NativeArray<Entity> entities = m_LineQuery().ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!DispatchLineEligibility.IsDispatchTransportLine(m_EntityManager, line))
                        continue;

                    string name = Name(line);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = "Line " + line.Index;
                    }

                    lines.Add(new WorkbenchLineRuntime
                    {
                        Entity = line,
                        Id = m_LineId(m_LineKey(line)),
                        Name = name
                    });
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            return lines;
        }

        internal List<WorkbenchLineRuntime> RuntimeLines()
        {
            List<WorkbenchLineRuntime> lines = new List<WorkbenchLineRuntime>();
            NativeArray<Entity> entities = m_LineQuery().ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (TryRuntimeLine(entities[i], out WorkbenchLineRuntime runtimeLine))
                    {
                        lines.Add(runtimeLine);
                    }
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            return lines;
        }

        internal NativeArray<Entity> LineEntities(Allocator allocator)
        {
            return m_LineQuery().ToEntityArray(allocator);
        }

        internal bool TryRuntimeLine(Entity line, out WorkbenchLineRuntime runtimeLine)
        {
            runtimeLine = null;
            if (!DispatchLineEligibility.IsDispatchTransportLine(m_EntityManager, line))
            {
                return false;
            }

            int routeNumber = int.MaxValue;
            if (m_EntityManager.HasComponent<RouteNumber>(line))
            {
                routeNumber = m_EntityManager.GetComponentData<RouteNumber>(line).m_Number;
            }

            string originStationId;
            string originStationName;
            LineOrigin(line, out originStationId, out originStationName);

            LineDispatchSupport support = RouteWaypointEndpointResolver.ComputeLineDispatchSupport(
                m_EntityManager, line, m_Stop);

            string name = Name(line);
            if (string.IsNullOrEmpty(name))
            {
                name = routeNumber != int.MaxValue
                    ? ("Line " + routeNumber.ToString())
                    : ("Line " + line.Index.ToString());
            }

            string originStatus = support.Supported ? string.Empty : "error";
            string originMessageKey = support.Supported
                ? string.Empty
                : (support.Reason == LineDispatchSupport.ReasonOriginOutsideEndpoint
                    ? "nativeSchedule.origin.unsupportedOutsideEndpoint"
                    : "nativeSchedule.origin.unsupportedNotPassengerStop");

            runtimeLine = new WorkbenchLineRuntime
            {
                Entity = line,
                Id = m_LineId(m_LineKey(line)),
                Name = name,
                Kind = TransportModeProfile.GetProfile(m_LineKey(line)).Lifecycle == LifecycleKind.Road
                    ? "local"
                    : string.Empty,
                RouteNumber = routeNumber,
                StationCount = CountStops(line),
                TransportType = m_TransportType(line),
                OriginStationId = originStationId,
                OriginStationName = originStationName,
                DispatchSupported = support.Supported,
                UnsupportedReason = support.Reason ?? string.Empty,
                OriginStatus = originStatus,
                OriginMessageKey = originMessageKey
            };
            runtimeLine.StableSignature = StableRuntimeSignature(runtimeLine);
            return true;
        }

        internal HashSet<string> LineIds()
        {
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);
            NativeArray<Entity> entities = m_LineQuery().ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!DispatchLineEligibility.IsDispatchTransportLine(m_EntityManager, line))
                        continue;

                    string lineId = m_LineId(m_LineKey(line));
                    if (!string.IsNullOrEmpty(lineId))
                    {
                        lineIds.Add(lineId);
                    }
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            return lineIds;
        }

        internal List<DispatchWorkbenchStationDto> Stations(Entity line)
        {
            List<DispatchWorkbenchStationDto> stations = new List<DispatchWorkbenchStationDto>();
            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            float cumulativeDistance = 0f;
            float3 previousPosition = float3.zero;
            bool hasPrevious = false;
            HashSet<Entity> seenStopEntities = new HashSet<Entity>();

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stopEntity = Stop(waypoint);
                if (stopEntity == Entity.Null || !seenStopEntities.Add(stopEntity))
                {
                    continue;
                }

                Entity positionEntity = waypoint;
                if (waypoint != Entity.Null
                    && m_EntityManager.Exists(waypoint)
                    && m_EntityManager.HasComponent<Connected>(waypoint))
                {
                    Entity connected = m_EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                    if (connected != Entity.Null
                        && m_EntityManager.Exists(connected)
                        && m_EntityManager.HasComponent<Transform>(connected))
                    {
                        positionEntity = connected;
                    }
                }

                if (!m_EntityManager.HasComponent<Transform>(positionEntity))
                    continue;

                float3 position = m_EntityManager.GetComponentData<Transform>(positionEntity).m_Position;
                if (hasPrevious)
                {
                    cumulativeDistance += math.distance(previousPosition, position);
                }

                previousPosition = position;
                hasPrevious = true;

                string name = StationName(stopEntity);
                if (string.IsNullOrEmpty(name))
                {
                    name = "Stop " + (stations.Count + 1).ToString();
                }

                stations.Add(new DispatchWorkbenchStationDto
                {
                    id = StationId(stations.Count),
                    name = name,
                    order = stations.Count,
                    distance = (float)Math.Round(cumulativeDistance, 1),
                    hasSiding = false
                });
            }

            return stations;
        }

        internal List<DispatchWorkbenchDepotDto> Depots()
        {
            List<DispatchWorkbenchDepotDto> depots = new List<DispatchWorkbenchDepotDto>();
            HashSet<Entity> seenCanonicalDepots = new HashSet<Entity>();
            NativeArray<Entity> depotEntities = m_DepotQuery().ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity rawDepot = depotEntities[i];
                    if (m_EntityManager.HasComponent<Game.Buildings.ServiceUpgrade>(rawDepot))
                        continue;

                    Entity depot = m_DepotCanon(rawDepot);
                    if (depot == Entity.Null || !seenCanonicalDepots.Add(depot))
                        continue;

                    string name = Name(depot);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = "Depot " + depot.Index;
                    }

                    string transportType = string.Empty;
                    Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                    if (prefab != Entity.Null && m_EntityManager.HasComponent<TransportDepotData>(prefab))
                    {
                        transportType = m_EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType.ToString();
                    }

                    depots.Add(new DispatchWorkbenchDepotDto
                    {
                        id = DepotId(depot),
                        name = name,
                        transportType = transportType
                    });
                }
            }
            finally
            {
                if (depotEntities.IsCreated) depotEntities.Dispose();
            }

            return depots
                .OrderBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.id, StringComparer.Ordinal)
                .ToList();
        }

        internal NativeArray<Entity> DepotEntities(Allocator allocator)
        {
            return m_DepotQuery().ToEntityArray(allocator);
        }

        internal bool TryDepot(Entity rawDepot, HashSet<Entity> seenCanonicalDepots, out DispatchWorkbenchDepotDto depotDto)
        {
            depotDto = null;
            if (rawDepot == Entity.Null
                || !m_EntityManager.Exists(rawDepot)
                || m_EntityManager.HasComponent<Game.Buildings.ServiceUpgrade>(rawDepot))
            {
                return false;
            }

            Entity depot = m_DepotCanon(rawDepot);
            if (depot == Entity.Null || (seenCanonicalDepots != null && !seenCanonicalDepots.Add(depot)))
            {
                return false;
            }

            string name = Name(depot);
            if (string.IsNullOrEmpty(name))
            {
                name = "Depot " + depot.Index;
            }

            string transportType = string.Empty;
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
            if (prefab != Entity.Null && m_EntityManager.HasComponent<TransportDepotData>(prefab))
            {
                transportType = m_EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType.ToString();
            }

            depotDto = new DispatchWorkbenchDepotDto
            {
                id = DepotId(depot),
                name = name,
                transportType = transportType
            };
            return true;
        }

        internal void LineOrigin(Entity line, out string originStationId, out string originStationName)
        {
            originStationId = string.Empty;
            originStationName = string.Empty;

            if (!m_EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0)
                return;

            Entity firstWaypoint = waypoints[0].m_Waypoint;
            if (RouteWaypointEndpointResolver.TryResolveRouteWaypointEndpoint(m_EntityManager, firstWaypoint, out _))
                return;

            Entity stopEntity = Stop(firstWaypoint);
            if (stopEntity == Entity.Null)
                return;

            Entity anchor = Anchor(stopEntity);
            originStationId = anchor != Entity.Null && anchor != stopEntity
                ? "station-building-" + anchor.Index.ToString()
                : Stops.OriginId(stopEntity);
            originStationName = StationName(stopEntity);
        }

        internal string Name(Entity entity)
        {
            return m_Name(entity) ?? string.Empty;
        }

        internal string StationName(Entity stopEntity)
        {
            return m_StationName(stopEntity) ?? string.Empty;
        }

        internal Entity Stop(Entity waypoint)
        {
            return m_Stop(waypoint);
        }

        internal Entity Anchor(Entity entity)
        {
            return m_Anchor(entity);
        }

        internal string Sak(Entity anchor)
        {
            return m_Sak(anchor) ?? string.Empty;
        }

        internal string StableRuntimeSignature(WorkbenchLineRuntime runtimeLine)
        {
            if (runtimeLine == null
                || runtimeLine.Entity == Entity.Null
                || !m_EntityManager.Exists(runtimeLine.Entity))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("entity=").Append(runtimeLine.Entity.Index);
            // RouteNumber is mutable display metadata. XTM may change it or allow duplicates;
            // the Lak-backed line id remains the identity and must keep timetable state.
            sb.Append("|transport=").Append(runtimeLine.TransportType ?? string.Empty);
            sb.Append("|origin=").Append(runtimeLine.OriginStationId ?? string.Empty);
            sb.Append("|supported=").Append(runtimeLine.DispatchSupported ? '1' : '0');
            sb.Append("|reason=").Append(runtimeLine.UnsupportedReason ?? string.Empty);
            sb.Append("|stations=").Append(runtimeLine.StationCount);
            sb.Append("|stops=");
            AppendStopOrderSignature(runtimeLine.Entity, sb);
            return sb.ToString();
        }

        internal static string StationId(int order)
        {
            return "station-" + order.ToString();
        }

        internal string DepotId(Entity depot)
        {
            return RawDepotId(m_DepotCanon(depot));
        }

        internal string RawDepotId(Entity depot)
        {
            if (depot == Entity.Null || !m_EntityManager.Exists(depot))
                return string.Empty;

            string transportType = string.Empty;
            int prefabIndex = 0;
            if (m_EntityManager.HasComponent<PrefabRef>(depot))
            {
                Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                prefabIndex = prefab.Index;
                if (prefab != Entity.Null && m_EntityManager.HasComponent<TransportDepotData>(prefab))
                {
                    transportType = m_EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType.ToString();
                }
            }

            int x = 0;
            int y = 0;
            int z = 0;
            if (m_EntityManager.HasComponent<Transform>(depot))
            {
                float3 position = m_EntityManager.GetComponentData<Transform>(depot).m_Position;
                x = (int)math.round(position.x);
                y = (int)math.round(position.y);
                z = (int)math.round(position.z);
            }

            return "depot:"
                + (string.IsNullOrEmpty(transportType) ? "-" : transportType.ToLowerInvariant())
                + ":prefab-" + prefabIndex.ToString()
                + ":x" + x.ToString()
                + ":y" + y.ToString()
                + ":z" + z.ToString();
        }

        internal Entity DepotById(string depotId)
        {
            if (string.IsNullOrEmpty(depotId))
                return Entity.Null;

            string fallbackKey = DepotLocationKey(depotId);
            Entity fallbackDepot = Entity.Null;
            bool fallbackAmbiguous = false;
            NativeArray<Entity> depotEntities = m_DepotQuery().ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity rawDepot = depotEntities[i];
                    Entity canonicalDepot = m_DepotCanon(rawDepot);
                    Entity resolvedDepot = canonicalDepot != Entity.Null ? canonicalDepot : rawDepot;
                    string canonicalId = DepotId(canonicalDepot);
                    string rawId = RawDepotId(rawDepot);
                    if (string.Equals(canonicalId, depotId, StringComparison.Ordinal)
                        || string.Equals(rawId, depotId, StringComparison.Ordinal))
                    {
                        return resolvedDepot;
                    }

                    if (string.IsNullOrEmpty(fallbackKey)
                        || (!string.Equals(DepotLocationKey(canonicalId), fallbackKey, StringComparison.Ordinal)
                            && !string.Equals(DepotLocationKey(rawId), fallbackKey, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (fallbackDepot == Entity.Null)
                    {
                        fallbackDepot = resolvedDepot;
                    }
                    else if (fallbackDepot != resolvedDepot)
                    {
                        fallbackAmbiguous = true;
                    }
                }
            }
            finally
            {
                if (depotEntities.IsCreated) depotEntities.Dispose();
            }

            if (fallbackDepot == Entity.Null || fallbackAmbiguous)
                return Entity.Null;

            return fallbackDepot;
        }

        private static string DepotLocationKey(string depotId)
        {
            if (string.IsNullOrWhiteSpace(depotId))
                return string.Empty;

            string[] parts = depotId.Split(':');
            if (parts.Length != 6
                || !string.Equals(parts[0], "depot", StringComparison.Ordinal)
                || !parts[2].StartsWith("prefab-", StringComparison.Ordinal)
                || !parts[3].StartsWith("x", StringComparison.Ordinal)
                || !parts[4].StartsWith("y", StringComparison.Ordinal)
                || !parts[5].StartsWith("z", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return parts[0]
                + ":" + parts[1]
                + ":" + parts[3]
                + ":" + parts[4]
                + ":" + parts[5];
        }

        private int CountStops(Entity line)
        {
            if (!m_EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return 0;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            HashSet<Entity> seenStopEntities = new HashSet<Entity>();

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = Stop(waypoints[i].m_Waypoint);
                if (stopEntity != Entity.Null)
                {
                    seenStopEntities.Add(stopEntity);
                }
            }

            return seenStopEntities.Count;
        }

        private void AppendStopOrderSignature(Entity line, StringBuilder sb)
        {
            if (!m_EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                sb.Append("none");
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            Entity lastStopEntity = Entity.Null;
            bool appended = false;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = Stop(waypoints[i].m_Waypoint);
                if (stopEntity == Entity.Null || stopEntity == lastStopEntity)
                {
                    continue;
                }

                lastStopEntity = stopEntity;
                if (appended)
                {
                    sb.Append('>');
                }

                sb.Append("s:").Append(stopEntity.Index);
                sb.Append("|o:").Append(Stops.OriginId(stopEntity) ?? string.Empty);
                Entity anchor = Anchor(stopEntity);
                if (anchor != Entity.Null && anchor != stopEntity)
                {
                    sb.Append("|b:").Append(anchor.Index);
                    string sak = Sak(anchor);
                    if (!string.IsNullOrEmpty(sak))
                    {
                        sb.Append("|sak:").Append(sak);
                    }
                }

                appended = true;
            }

            if (!appended)
            {
                sb.Append("empty");
            }
        }
    }
}
