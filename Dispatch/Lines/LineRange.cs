using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineRange
    {
        private readonly EntityManager m_EntityManager;
        private readonly LineTimes m_Times;

        public LineRange(EntityManager entityManager, LineTimes times)
        {
            m_EntityManager = entityManager;
            m_Times = times;
        }

        public bool NeedsDepot(Entity vehicle, Entity line)
        {
            if (m_EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                && (m_EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                    & PublicTransportFlags.RequiresMaintenance) != 0)
                return true;
            return m_Times.TryLineMeters(line, out float meters) && Left(vehicle) < 2f * meters;
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

    }
}
