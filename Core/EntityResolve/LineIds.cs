using System;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LineIds
    {
        private readonly EntityManager m_EntityManager;
        private readonly LineAnchorCatalog m_Anchors;

        internal LineIds(EntityManager entityManager, LineAnchorCatalog anchors)
        {
            m_EntityManager = entityManager;
            m_Anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        }

        internal string Id(LineKey key)
        {
            return LineIdentityService.GetId(key);
        }

        internal LineKey Key(string lineId)
        {
            return LineIdentityService.GetKey(lineId);
        }

        internal LineKey StableKey(Entity line)
        {
            return LineIdentityService.StableKey(m_Anchors, line);
        }

        internal string StableId(Entity line)
        {
            return LineIdentityService.GetId(StableKey(line));
        }

        internal string Type(Entity line)
        {
            if (line == Entity.Null || !m_EntityManager.Exists(line))
                return string.Empty;

            if (m_EntityManager.HasComponent<TransportLineData>(line))
                return m_EntityManager.GetComponentData<TransportLineData>(line).m_TransportType.ToString();

            if (!m_EntityManager.HasComponent<PrefabRef>(line))
                return string.Empty;

            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !m_EntityManager.HasComponent<TransportLineData>(prefab))
                return string.Empty;

            return m_EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType.ToString();
        }

        internal string Color(Entity line)
        {
            if (line == Entity.Null
                || !m_EntityManager.Exists(line)
                || !m_EntityManager.HasComponent<Game.Routes.Color>(line))
            {
                return string.Empty;
            }

            UnityEngine.Color32 color = m_EntityManager.GetComponentData<Game.Routes.Color>(line).m_Color;
            return "#" + color.r.ToString("X2") + color.g.ToString("X2") + color.b.ToString("X2");
        }
    }
}
