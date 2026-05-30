using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleLabels
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public RuntimeVehicleLabels(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Set(Entity vehicle, string message)
        {
            var fixedMessage = new FixedString64Bytes(message);
            if (!m_Runtime.m_UICache.TryGetValue(vehicle, out var cached) || cached != fixedMessage)
            {
                m_Runtime.m_NameSystem.SetCustomName(vehicle, message);
                m_Runtime.m_UICache[vehicle] = fixedMessage;
            }
        }
    }
}
