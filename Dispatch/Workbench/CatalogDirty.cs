using System;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.UI;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class CatalogDirty
    {
        private readonly EntityQuery m_LineDirtyQuery;
        private readonly EntityQuery m_StopDirtyQuery;
        private readonly EntityQuery m_DepotDirtyQuery;
        private readonly EntityQuery m_ConnectedDirtyQuery;
        private readonly EntityQuery m_LineNameDirtyQuery;
        private readonly EntityQuery m_StopNameDirtyQuery;
        private readonly EntityQuery m_DepotNameDirtyQuery;
        private readonly EntityQuery m_BuildingNameDirtyQuery;
        private readonly Action m_MarkDirty;
        private bool m_WasDirty;

        internal CatalogDirty(
            EntityManager entityManager,
            Action markDirty)
        {
            m_MarkDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));

            m_LineDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_StopDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Routes.TransportStop>() },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_DepotDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.TransportDepot>() },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_ConnectedDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Connected>(),
                    ComponentType.ReadOnly<Waypoint>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: false)
            });
            m_LineNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<TransportLine>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_StopNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_DepotNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Game.Buildings.TransportDepot>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_BuildingNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Building>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
        }

        internal void Reset()
        {
            m_WasDirty = false;
        }

        internal void Check(uint nowFrame)
        {
            bool dirty = IsDirty();
            if (dirty)
            {
                if (!m_WasDirty)
                {
                    m_MarkDirty();
                }
                m_WasDirty = true;
            }
            else
            {
                m_WasDirty = false;
            }
        }

        private bool IsDirty()
        {
            return !m_LineDirtyQuery.IsEmptyIgnoreFilter
                || !m_StopDirtyQuery.IsEmptyIgnoreFilter
                || !m_DepotDirtyQuery.IsEmptyIgnoreFilter
                || !m_ConnectedDirtyQuery.IsEmptyIgnoreFilter
                || !m_LineNameDirtyQuery.IsEmptyIgnoreFilter
                || !m_StopNameDirtyQuery.IsEmptyIgnoreFilter
                || !m_DepotNameDirtyQuery.IsEmptyIgnoreFilter
                || !m_BuildingNameDirtyQuery.IsEmptyIgnoreFilter;
        }

        private static ComponentType[] DirtyMarkers(bool includeBatchesUpdated)
        {
            if (!includeBatchesUpdated)
            {
                return new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Updated>()
                };
            }

            return new[]
            {
                ComponentType.ReadOnly<Created>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Updated>(),
                ComponentType.ReadOnly<BatchesUpdated>()
            };
        }
    }
}
