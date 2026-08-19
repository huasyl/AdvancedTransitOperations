using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class Stops
    {
        private const string AnchorPrefix = "sak:";

        private readonly EntityManager m_EntityManager;
        private readonly Names m_Names;
        private readonly Func<Entity, bool> m_Live;

        internal Stops(EntityManager entityManager, Names names, Func<Entity, bool> live = null)
        {
            m_EntityManager = entityManager;
            m_Names = names ?? throw new ArgumentNullException(nameof(names));
            m_Live = live;
        }

        internal static bool IsKey(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith(AnchorPrefix, StringComparison.Ordinal);
        }

        internal static string NewKey()
        {
            return AnchorPrefix + Guid.NewGuid().ToString("N");
        }

        internal Entity Stop(Entity waypoint)
        {
            if (!Live(waypoint))
            {
                return Entity.Null;
            }

            if (m_EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = m_EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (Live(connected))
                {
                    Entity stopEntity = Owned(connected);
                    if (stopEntity != Entity.Null)
                    {
                        return stopEntity;
                    }
                }
            }

            Entity waypointStop = Owned(waypoint);
            return waypointStop != Entity.Null ? waypointStop : Entity.Null;
        }

        internal Entity Building(Entity waypoint)
        {
            if (!Live(waypoint))
            {
                return Entity.Null;
            }

            if (m_EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = m_EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (Live(connected))
                {
                    Entity connectedStation = Station(connected);
                    if (connectedStation != Entity.Null)
                    {
                        return connectedStation;
                    }
                }
            }

            Entity waypointStation = Station(waypoint);
            return waypointStation != Entity.Null ? waypointStation : Entity.Null;
        }

        internal Entity Anchor(Entity entity)
        {
            if (!Live(entity))
            {
                return Entity.Null;
            }

            Entity stopEntity = m_EntityManager.HasComponent<Game.Routes.TransportStop>(entity)
                ? entity
                : Stop(entity);
            if (stopEntity != Entity.Null)
            {
                Entity building = Station(stopEntity);
                if (building != Entity.Null)
                {
                    return building;
                }

                return stopEntity;
            }

            Entity buildingEntity = Building(entity);
            return buildingEntity != Entity.Null ? buildingEntity : Entity.Null;
        }

        internal Entity AnchorOf(Entity stopEntity)
        {
            if (!Live(stopEntity))
            {
                return Entity.Null;
            }

            Entity building = Station(stopEntity);
            if (building != Entity.Null)
            {
                return building;
            }

            return stopEntity;
        }

        internal string Key(Entity anchor)
        {
            if (!Live(anchor)
                || !m_EntityManager.HasComponent<Sak>(anchor))
            {
                return string.Empty;
            }

            return m_EntityManager.GetComponentData<Sak>(anchor).Value.ToString();
        }

        internal string EnsureKey(Entity anchor)
        {
            if (!Live(anchor))
            {
                return string.Empty;
            }

            if (m_EntityManager.HasComponent<Sak>(anchor))
            {
                string current = m_EntityManager.GetComponentData<Sak>(anchor).Value.ToString();
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return current;
                }

                string repaired = NewKey();
                m_EntityManager.SetComponentData(anchor, new Sak
                {
                    Value = repaired
                });
                return repaired;
            }

            string created = NewKey();
            m_EntityManager.AddComponentData(anchor, new Sak
            {
                Value = created
            });
            return created;
        }

        internal StopRef Ref(Entity waypoint)
        {
            Entity stopEntity = Stop(waypoint);
            if (stopEntity != Entity.Null)
            {
                return new StopRef(stopEntity, ResolvedStopKind.Stop);
            }

            Entity building = Building(waypoint);
            if (building != Entity.Null)
            {
                return new StopRef(building, ResolvedStopKind.Building);
            }

            return new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal StopRef Ref(Entity waypoint, StopRef fallback)
        {
            StopRef resolved = Ref(waypoint);
            if (resolved.Ent != Entity.Null)
            {
                return resolved;
            }

            return Live(fallback.Ent)
                ? fallback
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal string Name(Entity entity, ResolvedStopKind kind)
        {
            if (entity == Entity.Null)
            {
                return string.Empty;
            }

            if (kind == ResolvedStopKind.Building)
            {
                return m_Names.Get(entity);
            }

            return StationName(entity);
        }

        internal string Id(Entity entity, ResolvedStopKind kind)
        {
            if (entity == Entity.Null)
            {
                return string.Empty;
            }

            return kind == ResolvedStopKind.Building
                ? BuildingId(entity)
                : OriginId(entity);
        }

        internal string StationName(Entity stopEntity)
        {
            if (!Live(stopEntity))
                return string.Empty;

            Entity anchor = Anchor(stopEntity);
            if (anchor != Entity.Null && anchor != stopEntity)
            {
                string buildingName = m_Names.Get(anchor);
                if (!string.IsNullOrEmpty(buildingName))
                {
                    return buildingName;
                }
            }

            return m_Names.Get(stopEntity);
        }

        internal string StationRenderedName(Entity stopEntity)
        {
            if (!Live(stopEntity))
                return string.Empty;

            Entity anchor = Anchor(stopEntity);
            if (anchor != Entity.Null && anchor != stopEntity)
            {
                string rendered = m_Names.Rendered(anchor);
                if (!string.IsNullOrEmpty(rendered))
                    return rendered;
            }

            return m_Names.Rendered(stopEntity);
        }

        internal Entity Owned(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (!Live(current))
                {
                    break;
                }

                if (m_EntityManager.HasComponent<Game.Routes.TransportStop>(current))
                {
                    return current;
                }

                if (!m_EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = m_EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        internal Entity Station(Entity stop)
        {
            Entity current = stop;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (!Live(current))
                {
                    break;
                }

                if (m_EntityManager.HasComponent<TransportStation>(current))
                {
                    if (m_EntityManager.HasComponent<Owner>(current))
                    {
                        Entity owner = m_EntityManager.GetComponentData<Owner>(current).m_Owner;
                        if (owner != Entity.Null)
                        {
                            return owner;
                        }
                    }

                    return current;
                }

                if (!m_EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = m_EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        internal string OriginGroup(Entity stopEntity)
        {
            if (stopEntity == Entity.Null)
            {
                return string.Empty;
            }

            Entity buildingEntity = Station(stopEntity);
            if (buildingEntity != Entity.Null)
            {
                return "station-building-" + buildingEntity.Index.ToString();
            }

            return OriginId(stopEntity);
        }

        internal static string OriginId(Entity stopEntity)
        {
            if (stopEntity == Entity.Null)
            {
                return string.Empty;
            }

            return "station-" + stopEntity.Index.ToString();
        }

        internal static string BuildingId(Entity building)
        {
            if (building == Entity.Null)
            {
                return string.Empty;
            }

            return "station-building-" + building.Index.ToString();
        }

        internal int Count(Entity line)
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

        private bool Live(Entity entity)
        {
            if (m_Live != null)
            {
                return m_Live(entity);
            }

            return entity != Entity.Null && m_EntityManager.Exists(entity);
        }
    }
}
