namespace RapidTransitMod
{
    public sealed class RuntimeFeatureSettingsStore
    {
        private RuntimeFeatureSettingsState m_State = RuntimeFeatureSettingsState.Default();

        public bool DispatchEnabled => m_State.DispatchEnabled;
        public bool BypassEnabled => m_State.BypassEnabled;
        public bool BroadcastEnabled => m_State.BroadcastEnabled;
        public bool DepotLockEnabled => m_State.DepotLockEnabled;

        public RuntimeFeatureSettingsState Get()
        {
            return m_State.Clone();
        }

        public void Set(RuntimeFeatureSettingsState state)
        {
            m_State = Normalize(state);
        }

        public void Reset()
        {
            m_State = RuntimeFeatureSettingsState.Default();
        }

        private static RuntimeFeatureSettingsState Normalize(RuntimeFeatureSettingsState state)
        {
            if (state == null)
                return RuntimeFeatureSettingsState.Default();

            return new RuntimeFeatureSettingsState
            {
                DispatchEnabled = state.DispatchEnabled,
                BypassEnabled = state.BypassEnabled,
                BroadcastEnabled = state.BroadcastEnabled,
                DepotLockEnabled = state.DepotLockEnabled
            };
        }
    }

    public sealed class RuntimeFeatureSettingsState
    {
        public bool DispatchEnabled { get; set; } = true;
        public bool BypassEnabled { get; set; } = true;
        public bool BroadcastEnabled { get; set; } = true;
        public bool DepotLockEnabled { get; set; } = true;

        public RuntimeFeatureSettingsState Clone()
        {
            return new RuntimeFeatureSettingsState
            {
                DispatchEnabled = DispatchEnabled,
                BypassEnabled = BypassEnabled,
                BroadcastEnabled = BroadcastEnabled,
                DepotLockEnabled = DepotLockEnabled
            };
        }

        public static RuntimeFeatureSettingsState Default()
        {
            return new RuntimeFeatureSettingsState();
        }
    }
}
