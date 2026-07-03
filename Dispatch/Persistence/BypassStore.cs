using Unity.Entities;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class BypassStore
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public BypassStore(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Ensure()
        {
            if (m_Runtime.m_BypassStationBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<BypassStationSettingElement>(city))
                m_Runtime.EntityManager.AddBuffer<BypassStationSettingElement>(city);
            m_Runtime.m_BypassStationBufferReady = true;
        }
    }
}
