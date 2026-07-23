using System;
using System.Collections.Generic;

namespace RapidTransitMod
{
    public sealed class LineConfigStore
    {
        private readonly Dictionary<LineKey, LineConfigState> m_Lines =
            new Dictionary<LineKey, LineConfigState>();
        private ulong m_Version = 1;
        private Action<LineKey> m_LineChanged;
        private Action m_AllChanged;

        public ulong Version => m_Version;

        internal void SetDirtyCallbacks(Action<LineKey> lineChanged, Action allChanged)
        {
            m_LineChanged = lineChanged;
            m_AllChanged = allChanged;
        }

        public LineConfigState Get(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out LineConfigState state)
                && state != null)
            {
                return state.Clone();
            }

            return LineConfigState.Default(m_Version);
        }

        public LineConfigState Get(string lineId, TransitMode mode)
        {
            return Get(LineIdentityService.GetKey(lineId, mode));
        }

        public bool TryGet(LineKey lineKey, out LineConfigState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out LineConfigState stored)
                && stored != null)
            {
                state = stored.Clone();
                return true;
            }

            state = LineConfigState.Default(m_Version);
            return false;
        }

        public bool TryGet(string lineId, TransitMode mode, out LineConfigState state)
        {
            return TryGet(LineIdentityService.GetKey(lineId, mode), out state);
        }

        public void Set(LineKey lineKey, LineConfigState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty)
                throw new ArgumentException("lineKey", nameof(lineKey));

            m_Version++;
            if (state == null)
            {
                m_Lines.Remove(key);
                m_LineChanged?.Invoke(key);
                return;
            }

            LineConfigState normalized = Normalize(state, m_Version);
            m_Lines[key] = normalized;
            m_LineChanged?.Invoke(key);
        }

        public bool Clear(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty)
                return false;

            if (!m_Lines.Remove(key))
                return false;

            m_Version++;
            m_LineChanged?.Invoke(key);
            return true;
        }

        internal bool RestoreLegacy(LineKey lineKey, LineConfigState state)
        {
            if (!LineKey.IsLegacyNumericKey(lineKey) || state == null)
                return false;

            Set(lineKey, state);
            return true;
        }

        public void Clear()
        {
            if (m_Lines.Count == 0)
                return;

            m_Version++;
            m_Lines.Clear();
            m_AllChanged?.Invoke();
        }

        public void Clear(TransitMode mode)
        {
            if (mode == TransitMode.Unknown)
            {
                Clear();
                return;
            }

            List<LineKey> keys = new List<LineKey>();
            foreach (LineKey key in m_Lines.Keys)
            {
                if (key.Mode == mode)
                {
                    keys.Add(key);
                }
            }

            if (keys.Count == 0)
                return;

            m_Version++;
            for (int i = 0; i < keys.Count; i++)
            {
                m_Lines.Remove(keys[i]);
            }
            m_AllChanged?.Invoke();
        }

        public IEnumerable<KeyValuePair<LineKey, LineConfigState>> GetAll()
        {
            foreach (KeyValuePair<LineKey, LineConfigState> entry in m_Lines)
            {
                if (entry.Value == null)
                    continue;

                yield return new KeyValuePair<LineKey, LineConfigState>(
                    entry.Key,
                    entry.Value.Clone());
            }
        }

        public IEnumerable<KeyValuePair<LineKey, LineConfigState>> GetAll(TransitMode mode)
        {
            foreach (KeyValuePair<LineKey, LineConfigState> entry in GetAll())
            {
                if (mode != TransitMode.Unknown && entry.Key.Mode != mode)
                    continue;

                yield return entry;
            }
        }

        public bool PromoteLegacy(TransitMode mode, string lineId)
        {
            LineKey legacyKey = LineIdentityService.GetKey(lineId);
            LineKey targetKey = legacyKey.NormalizeForMode(mode);
            if (legacyKey.IsEmpty
                || legacyKey.Mode != TransitMode.Unknown
                || targetKey.IsEmpty
                || targetKey.Mode == TransitMode.Unknown
                || legacyKey == targetKey
                || m_Lines.ContainsKey(targetKey)
                || !m_Lines.TryGetValue(legacyKey, out LineConfigState state)
                || state == null)
            {
                return false;
            }

            m_Version++;
            m_Lines[targetKey] = Normalize(state, m_Version);
            m_Lines.Remove(legacyKey);
            m_AllChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Explicit one-shot move from a legacy numeric key (mode:n) to mode:guid32.
        /// Preserves hold/dwell/depot/kind via Normalize; does not invent mappings.
        /// </summary>
        public LineKeyMigrateResult Migrate(LineKey legacy, LineKey stable)
        {
            if (!LineKey.IsStableGuidKey(stable) || !LineKey.IsLegacyNumericKey(legacy))
                return LineKeyMigrateResult.Rejected;

            if (legacy.Mode == TransitMode.Unknown)
                return LineKeyMigrateResult.Rejected;

            if (legacy.Mode != stable.Mode)
                return LineKeyMigrateResult.ModeMismatch;

            if (legacy.Equals(stable))
                return LineKeyMigrateResult.SameKey;

            if (!m_Lines.TryGetValue(legacy, out LineConfigState state) || state == null)
                return LineKeyMigrateResult.MissingLegacy;

            if (m_Lines.ContainsKey(stable))
                return LineKeyMigrateResult.TargetOccupied;

            m_Version++;
            m_Lines[stable] = Normalize(state, m_Version);
            m_Lines.Remove(legacy);
            m_AllChanged?.Invoke();
            return LineKeyMigrateResult.Migrated;
        }

        private static LineConfigState Normalize(LineConfigState state, ulong version)
        {
            return new LineConfigState
            {
                OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.Hold(state.OriginHoldLimitMinutes),
                MaxStationDwellMinutes = RuntimeConfigStoreDefaults.Dwell(state.MaxStationDwellMinutes),
                AllowedDepotId = RuntimeConfigStoreDefaults.NormalizeAllowedDepotId(state.AllowedDepotId),
                ConfiguredServiceKind = RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind(state.ConfiguredServiceKind),
                SettingsVersion = version
            };
        }
    }

    public sealed class LineConfigState
    {
        public int OriginHoldLimitMinutes { get; set; } = RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes;
        public int MaxStationDwellMinutes { get; set; } = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes;
        public string AllowedDepotId { get; set; } = string.Empty;
        public string ConfiguredServiceKind { get; set; } = string.Empty;
        public ulong SettingsVersion { get; set; } = 1;

        public LineConfigState Clone()
        {
            return new LineConfigState
            {
                OriginHoldLimitMinutes = OriginHoldLimitMinutes,
                MaxStationDwellMinutes = MaxStationDwellMinutes,
                AllowedDepotId = AllowedDepotId ?? string.Empty,
                ConfiguredServiceKind = ConfiguredServiceKind ?? string.Empty,
                SettingsVersion = SettingsVersion
            };
        }

        public static LineConfigState Default(ulong version)
        {
            return new LineConfigState
            {
                SettingsVersion = version
            };
        }
    }
}
