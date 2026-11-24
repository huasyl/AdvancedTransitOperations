namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class RuntimeCache
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly RapidTransitMod.Dispatch.Observation.Buffers m_Buffers;
        private readonly MileageStore m_Mileage;

        public RuntimeCache(
            ModRuntimeHostSystem runtime,
            RapidTransitMod.Dispatch.Observation.Buffers buffers,
            MileageStore mileage)
        {
            m_Runtime = runtime;
            m_Buffers = buffers;
            m_Mileage = mileage;
        }

        public void Load()
        {
            m_Buffers.Load();
        }

        public void LoadDwell()
        {
            m_Buffers.LoadDwell();
        }

        public void LoadStationDwell()
        {
            m_Buffers.LoadStationDwell();
        }

        public void LoadBusSeg()
        {
            m_Buffers.LoadBusSeg();
        }

        public void Clear()
        {
            m_Runtime.m_DwellObservationBufferReady = false;
            m_Runtime.m_DwellObservationCacheLoaded = false;
            m_Runtime.m_StationDwellObservationBufferReady = false;
            m_Runtime.m_StationDwellObservationCacheLoaded = false;
            m_Runtime.m_BusSegObservationBufferReady = false;
            m_Runtime.m_BusSegObservationCacheLoaded = false;
        }

        public void Ensure()
        {
            m_Runtime.m_LapCache.Ensure();
            m_Runtime.m_VehicleCache.Ensure();
            m_Runtime.m_DispatchCache.Ensure();
            m_Mileage.Ensure();
            m_Buffers.Ensure();
        }
    }
}
