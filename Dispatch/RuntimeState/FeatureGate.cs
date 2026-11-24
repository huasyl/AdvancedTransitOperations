using System;

namespace RapidTransitMod
{
    internal sealed class FeatureGate
    {
        private readonly FeatureSettingsStore m_Store;
        private readonly Action m_StopBroadcast;
        private readonly Action m_StartBroadcast;
        private readonly Action m_DispatchChanged;
        private ulong m_DispatchGeneration;

        public FeatureGate(
            FeatureSettingsStore store,
            Action stopBroadcast,
            Action startBroadcast,
            Action dispatchChanged)
        {
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_StopBroadcast = stopBroadcast ?? throw new ArgumentNullException(nameof(stopBroadcast));
            m_StartBroadcast = startBroadcast ?? throw new ArgumentNullException(nameof(startBroadcast));
            m_DispatchChanged = dispatchChanged ?? throw new ArgumentNullException(nameof(dispatchChanged));
        }

        public RuntimeFeatureSettingsDto Dto()
        {
            return ToDto(m_Store.Get());
        }

        public bool Same(RuntimeFeatureSettingsDto settings)
        {
            if (settings == null)
                return true;

            FeatureSettingsState current = m_Store.Get();
            return current.DispatchEnabled == settings.dispatchEnabled
                && current.BroadcastEnabled == settings.broadcastEnabled
                && current.DepotLockEnabled == settings.depotLockEnabled;
        }

        public void Apply(RuntimeFeatureSettingsDto settings)
        {
            FeatureSettingsState previous = m_Store.Get();
            FeatureSettingsState next = ToState(settings);
            m_Store.Set(next);

            if (previous.DispatchEnabled != next.DispatchEnabled)
            {
                m_DispatchGeneration++;
                m_DispatchChanged();
            }

            if (previous.BroadcastEnabled && !next.BroadcastEnabled)
            {
                m_StopBroadcast();
            }
            else if (!previous.BroadcastEnabled && next.BroadcastEnabled)
            {
                m_StartBroadcast();
            }
        }

        public void Reset()
        {
            bool wasEnabled = m_Store.DispatchEnabled;
            m_Store.Reset();
            if (wasEnabled != m_Store.DispatchEnabled)
            {
                m_DispatchGeneration++;
                m_DispatchChanged();
            }
        }

        public ulong DispatchGeneration => m_DispatchGeneration;

        public bool Dispatch()
        {
            return m_Store.DispatchEnabled;
        }

        public bool Broadcast()
        {
            return m_Store.BroadcastEnabled;
        }

        public bool DepotLock()
        {
            return m_Store.DepotLockEnabled;
        }

        private static RuntimeFeatureSettingsDto ToDto(FeatureSettingsState state)
        {
            FeatureSettingsState normalized = state ?? FeatureSettingsState.Default();
            return new RuntimeFeatureSettingsDto
            {
                dispatchEnabled = normalized.DispatchEnabled,
                broadcastEnabled = normalized.BroadcastEnabled,
                depotLockEnabled = normalized.DepotLockEnabled
            };
        }

        private static FeatureSettingsState ToState(RuntimeFeatureSettingsDto settings)
        {
            if (settings == null)
                return FeatureSettingsState.Default();

            return new FeatureSettingsState
            {
                DispatchEnabled = settings.dispatchEnabled,
                BroadcastEnabled = settings.broadcastEnabled,
                DepotLockEnabled = settings.depotLockEnabled
            };
        }
    }
}
