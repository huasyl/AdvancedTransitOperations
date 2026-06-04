namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class RuntimeCache
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly RapidTransitMod.Dispatch.Observation.Buffers m_Buffers;
        private readonly MileageStore m_Mileage;
        private readonly BypassStore m_Bypass;

        public RuntimeCache(
            DispatchRuntimeSystem runtime,
            RapidTransitMod.Dispatch.Observation.Buffers buffers,
            MileageStore mileage,
            BypassStore bypass)
        {
            m_Runtime = runtime;
            m_Buffers = buffers;
            m_Mileage = mileage;
            m_Bypass = bypass;
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

        public void LoadSlice()
        {
            m_Buffers.LoadSlice();
        }

        public void Clear()
        {
            m_Runtime.m_DwellObservationBufferReady = false;
            m_Runtime.m_DwellObservationCacheLoaded = false;
            m_Runtime.m_StationDwellObservationBufferReady = false;
            m_Runtime.m_StationDwellObservationCacheLoaded = false;
            m_Runtime.m_TraversalSliceObservationBufferReady = false;
            m_Runtime.m_TraversalSliceObservationCacheLoaded = false;
        }

        public void Ensure()
        {
            m_Runtime.m_LapCache.Ensure();
            m_Runtime.m_VehicleCache.Ensure();
            m_Runtime.m_DispatchCache.Ensure();
            m_Mileage.Ensure();
            m_Bypass.Ensure();
            m_Buffers.Ensure();
        }
    }
}
