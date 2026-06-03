using Game.Prefabs;
using Unity.Entities;

namespace RapidTransitMod
{
    public static class DepotCompatibilityService
    {
        public static bool Match(TransportType lineType, TransportType depotType)
        {
            return lineType == depotType
                && lineType != TransportType.None
                && lineType != TransportType.Count;
        }

        public static bool Match(TransportLineData lineData, TransportDepotData depotData)
        {
            return Match(lineData.m_TransportType, depotData.m_TransportType);
        }

        public static bool Match(EntityManager entityManager, Entity line, Entity depot)
        {
            if (line == Entity.Null
                || depot == Entity.Null
                || !entityManager.Exists(line)
                || !entityManager.Exists(depot)
                || !entityManager.HasComponent<PrefabRef>(line)
                || !entityManager.HasComponent<PrefabRef>(depot))
            {
                return false;
            }

            Entity linePrefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            Entity depotPrefab = entityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
            if (linePrefab == Entity.Null
                || depotPrefab == Entity.Null
                || !entityManager.HasComponent<TransportLineData>(linePrefab)
                || !entityManager.HasComponent<TransportDepotData>(depotPrefab))
            {
                return false;
            }

            TransportLineData lineData = entityManager.GetComponentData<TransportLineData>(linePrefab);
            TransportDepotData depotData = entityManager.GetComponentData<TransportDepotData>(depotPrefab);
            return Match(lineData, depotData);
        }
    }
}
