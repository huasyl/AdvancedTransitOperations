using Game.Prefabs;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Observation;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineRange
    {
        private readonly EntityManager m_EntityManager;
        private readonly Query m_Obs;
        private readonly float m_Threshold;

        public LineRange(EntityManager entityManager, Query obs, float threshold)
        {
            m_EntityManager = entityManager;
            m_Obs = obs;
            m_Threshold = threshold;
        }

        public float Left(Entity vehicle)
        {
            if (!m_EntityManager.HasComponent<Odometer>(vehicle)) return float.MaxValue;
            if (!m_EntityManager.HasComponent<PrefabRef>(vehicle)) return float.MaxValue;
            float current = m_EntityManager.GetComponentData<Odometer>(vehicle).m_Distance;
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!m_EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return float.MaxValue;
            float range = m_EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            return range > 0f ? range - current : float.MaxValue;
        }

        public bool Needs(Entity vehicle)
        {
            var publicTransport = m_EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.RequiresMaintenance) != 0) return true;
            if (!m_EntityManager.HasComponent<Odometer>(vehicle) || !m_EntityManager.HasComponent<PrefabRef>(vehicle)) return false;
            float distance = m_EntityManager.GetComponentData<Odometer>(vehicle).m_Distance;
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!m_EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return false;
            float range = m_EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            return range > 0f && distance / range >= m_Threshold;
        }

        public bool CanFinish(Entity vehicle)
        {
            if (!m_EntityManager.HasComponent<Odometer>(vehicle) || !m_EntityManager.HasComponent<PrefabRef>(vehicle)) return true;
            float current = m_EntityManager.GetComponentData<Odometer>(vehicle).m_Distance;
            Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!m_EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return true;
            float range = m_EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            if (range <= 0f) return true;
            float remaining = range - current;
            if (m_Obs.TryLapDistance(vehicle, out float lapDistance) && lapDistance > 0f)
                return remaining >= lapDistance;
            return current / range < m_Threshold;
        }
    }
}
