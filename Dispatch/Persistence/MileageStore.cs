using Game.Common;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class MileageStore
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public MileageStore(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Ensure()
        {
            if (m_Runtime.m_LineMileageBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<LineMileageModelStateElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineMileageModelStateElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<LineMileageAnchorElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineMileageAnchorElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<LineCorridorStateElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineCorridorStateElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<LineCorridorNodeElement>(city))
                m_Runtime.EntityManager.AddBuffer<LineCorridorNodeElement>(city);
            m_Runtime.m_LineMileageBufferReady = true;
        }

        public bool TryWaypointPosition(Entity waypoint, out float3 position)
        {
            Entity positionEntity = waypoint;
            if (m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connected != Entity.Null && m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(connected))
                {
                    positionEntity = connected;
                }
            }

            if (!m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(positionEntity))
            {
                position = float3.zero;
                return false;
            }

            position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(positionEntity).m_Position;
            return true;
        }
    }
}
