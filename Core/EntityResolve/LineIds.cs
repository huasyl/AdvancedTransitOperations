using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LineIds
    {
        private readonly EntityManager m_EntityManager;

        internal LineIds(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        internal string Get(Entity line)
        {
            return LineIdentityService.GetId(LineIdentityService.GetKey(m_EntityManager, line));
        }

        internal string Id(LineKey key)
        {
            return LineIdentityService.GetId(key);
        }

        internal LineKey Key(string lineId)
        {
            return LineIdentityService.GetKey(lineId);
        }

        internal LineKey Key(Entity line)
        {
            return Key(line, null);
        }

        internal LineKey Key(Entity line, string fallbackLineId = null)
        {
            LineKey key = LineIdentityService.GetKey(m_EntityManager, line);
            return !key.IsEmpty ? key : LineIdentityService.GetKey(fallbackLineId);
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
