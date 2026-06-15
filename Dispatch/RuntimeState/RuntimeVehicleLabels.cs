using Unity.Collections;
using Unity.Entities;
using System.Collections.Generic;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleLabels
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, string> m_LabelCache = new Dictionary<Entity, string>();

        public RuntimeVehicleLabels(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Set(Entity vehicle, string message)
        {
            message ??= string.Empty;
            if (m_LabelCache.TryGetValue(vehicle, out string cachedLabel)
                && string.Equals(cachedLabel, message, System.StringComparison.Ordinal))
            {
                return;
            }

            var fixedMessage = new FixedString64Bytes(message);
            if (!m_Runtime.m_UICache.TryGetValue(vehicle, out var cached) || cached != fixedMessage)
            {
                m_Runtime.m_NameSystem.SetCustomName(vehicle, message);
                m_Runtime.m_UICache[vehicle] = fixedMessage;
            }
            m_LabelCache[vehicle] = message;
        }

        public void SetLocalized(Entity vehicle, string key, string fallback, string suffix = "")
        {
            string message = Label(key, fallback) + (suffix ?? string.Empty);
            Set(vehicle, message);
        }

        private static string Label(string key, string fallback)
        {
            string localizationKey = "RapidTransit.VehicleLabel." + key;
            string translated = Names.Key(localizationKey);
            return string.Equals(translated, localizationKey, System.StringComparison.Ordinal)
                ? fallback ?? string.Empty
                : translated;
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_LabelCache.Remove(vehicle);
        }

        public void Clear()
        {
            m_LabelCache.Clear();
        }
    }
}
