using Unity.Collections;
using Unity.Entities;
using System.Collections.Generic;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleLabels
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, string> m_LabelCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, LocalizedLabelSpec> m_LocalizedSpecCache = new Dictionary<Entity, LocalizedLabelSpec>();
        private readonly Dictionary<string, string> m_LocalizedBaseCache = new Dictionary<string, string>();
        private object m_ActiveLocalizationDictionary;

        public RuntimeVehicleLabels(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Set(Entity vehicle, string message)
        {
            m_LocalizedSpecCache.Remove(vehicle);
            SetCore(vehicle, message);
        }

        private void SetCore(Entity vehicle, string message)
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
            SetLocalizedCore(vehicle, key, fallback, string.Empty, suffix);
        }

        public void SetPrefixedLocalized(Entity vehicle, string key, string fallback, string prefix, string suffix = "")
        {
            SetLocalizedCore(vehicle, key, fallback, prefix, suffix);
        }

        private void SetLocalizedCore(Entity vehicle, string key, string fallback, string prefix, string suffix)
        {
            EnsureLocalizationCacheFresh();
            key ??= string.Empty;
            fallback ??= string.Empty;
            prefix ??= string.Empty;
            suffix ??= string.Empty;

            LocalizedLabelSpec spec = new LocalizedLabelSpec(key, fallback, prefix, suffix);
            if (m_LocalizedSpecCache.TryGetValue(vehicle, out LocalizedLabelSpec cachedSpec)
                && cachedSpec.Equals(spec))
            {
                return;
            }

            string message = prefix + Label(key, fallback) + suffix;
            SetCore(vehicle, message);
            m_LocalizedSpecCache[vehicle] = spec;
        }

        private string Label(string key, string fallback)
        {
            string cacheKey = key + "\u001f" + fallback;
            if (m_LocalizedBaseCache.TryGetValue(cacheKey, out string cachedLabel))
            {
                return cachedLabel;
            }

            string localizationKey = "RapidTransit.VehicleLabel." + key;
            string translated = Names.Key(localizationKey);
            string label = string.Equals(translated, localizationKey, System.StringComparison.Ordinal)
                ? fallback ?? string.Empty
                : translated;
            m_LocalizedBaseCache[cacheKey] = label;
            return label;
        }

        private void EnsureLocalizationCacheFresh()
        {
            object activeDictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            if (ReferenceEquals(m_ActiveLocalizationDictionary, activeDictionary))
            {
                return;
            }

            m_ActiveLocalizationDictionary = activeDictionary;
            m_LocalizedBaseCache.Clear();
            m_LocalizedSpecCache.Clear();
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                m_LabelCache.Remove(vehicle);
                m_LocalizedSpecCache.Remove(vehicle);
            }
        }

        public void Clear()
        {
            m_LabelCache.Clear();
            m_LocalizedSpecCache.Clear();
            m_LocalizedBaseCache.Clear();
            m_ActiveLocalizationDictionary = null;
        }

        private readonly struct LocalizedLabelSpec
        {
            private readonly string m_Key;
            private readonly string m_Fallback;
            private readonly string m_Prefix;
            private readonly string m_Suffix;

            public LocalizedLabelSpec(string key, string fallback, string prefix, string suffix)
            {
                m_Key = key;
                m_Fallback = fallback;
                m_Prefix = prefix;
                m_Suffix = suffix;
            }

            public bool Equals(LocalizedLabelSpec other)
            {
                return string.Equals(m_Key, other.m_Key, System.StringComparison.Ordinal)
                    && string.Equals(m_Fallback, other.m_Fallback, System.StringComparison.Ordinal)
                    && string.Equals(m_Prefix, other.m_Prefix, System.StringComparison.Ordinal)
                    && string.Equals(m_Suffix, other.m_Suffix, System.StringComparison.Ordinal);
            }
        }
    }
}
