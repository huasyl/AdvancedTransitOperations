namespace RapidTransitMod
{
    public sealed class FeatureSettingsStore
    {
        private FeatureSettingsState m_State = FeatureSettingsState.Default();

        public bool DispatchEnabled => m_State.DispatchEnabled;
        public bool BypassEnabled => m_State.BypassEnabled;
        public bool BroadcastEnabled => m_State.BroadcastEnabled;
        public bool DepotLockEnabled => m_State.DepotLockEnabled;

        public FeatureSettingsState Get()
        {
            return m_State.Clone();
        }

        public void Set(FeatureSettingsState state)
        {
            m_State = Normalize(state);
        }

        public void Reset()
        {
            m_State = FeatureSettingsState.Default();
        }

        private static FeatureSettingsState Normalize(FeatureSettingsState state)
        {
            if (state == null)
                return FeatureSettingsState.Default();

            return new FeatureSettingsState
            {
                DispatchEnabled = state.DispatchEnabled,
                BypassEnabled = state.BypassEnabled,
                BroadcastEnabled = state.BroadcastEnabled,
                DepotLockEnabled = state.DepotLockEnabled
            };
        }
    }

    public sealed class FeatureSettingsState
    {
        public bool DispatchEnabled { get; set; } = true;
        public bool BypassEnabled { get; set; } = true;
        public bool BroadcastEnabled { get; set; } = true;
        public bool DepotLockEnabled { get; set; } = true;

        public FeatureSettingsState Clone()
        {
            return new FeatureSettingsState
            {
                DispatchEnabled = DispatchEnabled,
                BypassEnabled = BypassEnabled,
                BroadcastEnabled = BroadcastEnabled,
                DepotLockEnabled = DepotLockEnabled
            };
        }

        public static FeatureSettingsState Default()
        {
            return new FeatureSettingsState();
        }
    }
}
