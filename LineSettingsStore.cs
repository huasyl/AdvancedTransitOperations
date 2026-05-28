using System;
using System.Collections.Generic;

namespace RapidTransitMod
{
    public sealed class LineSettingsStore
    {
        private readonly Dictionary<LineKey, LineSettingsState> m_Lines =
            new Dictionary<LineKey, LineSettingsState>();
        private ulong m_Version = 1;

        public ulong Version => m_Version;

        public LineSettingsState Get(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out LineSettingsState state)
                && state != null)
            {
                return state.Clone();
            }

            return LineSettingsState.Default(m_Version);
        }

        public LineSettingsState Get(string lineId, TransitMode mode)
        {
            return Get(LineIdentityService.GetKey(lineId, mode));
        }

        public bool TryGet(LineKey lineKey, out LineSettingsState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out LineSettingsState stored)
                && stored != null)
            {
                state = stored.Clone();
                return true;
            }

            state = LineSettingsState.Default(m_Version);
            return false;
        }

        public bool TryGet(string lineId, TransitMode mode, out LineSettingsState state)
        {
            return TryGet(LineIdentityService.GetKey(lineId, mode), out state);
        }

        public void Set(LineKey lineKey, LineSettingsState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty)
                throw new ArgumentException("lineKey", nameof(lineKey));

            m_Version++;
            if (state == null)
            {
                m_Lines.Remove(key);
                return;
            }

            LineSettingsState normalized = Normalize(state, m_Version);
            m_Lines[key] = normalized;
        }

        public void Clear()
        {
            if (m_Lines.Count == 0)
                return;

            m_Version++;
            m_Lines.Clear();
        }

        public IEnumerable<KeyValuePair<LineKey, LineSettingsState>> GetAll()
        {
            foreach (KeyValuePair<LineKey, LineSettingsState> entry in m_Lines)
            {
                if (entry.Value == null)
                    continue;

                yield return new KeyValuePair<LineKey, LineSettingsState>(
                    entry.Key,
                    entry.Value.Clone());
            }
        }

        public IEnumerable<KeyValuePair<LineKey, LineSettingsState>> GetAll(TransitMode mode)
        {
            foreach (KeyValuePair<LineKey, LineSettingsState> entry in GetAll())
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
                || !m_Lines.TryGetValue(legacyKey, out LineSettingsState state)
                || state == null)
            {
                return false;
            }

            m_Version++;
            m_Lines[targetKey] = Normalize(state, m_Version);
            m_Lines.Remove(legacyKey);
            return true;
        }

        private static LineSettingsState Normalize(LineSettingsState state, ulong version)
        {
            return new LineSettingsState
            {
                OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.NormalizeOriginHoldLimitMinutes(state.OriginHoldLimitMinutes),
                MaxStationDwellMinutes = RuntimeConfigStoreDefaults.NormalizeMaxStationDwellMinutes(state.MaxStationDwellMinutes),
                AllowedDepotId = RuntimeConfigStoreDefaults.NormalizeAllowedDepotId(state.AllowedDepotId),
                ConfiguredServiceKind = RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind(state.ConfiguredServiceKind),
                SettingsVersion = version
            };
        }
    }

    public sealed class LineSettingsState
    {
        public int OriginHoldLimitMinutes { get; set; } = RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes;
        public int MaxStationDwellMinutes { get; set; } = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes;
        public string AllowedDepotId { get; set; } = string.Empty;
        public string ConfiguredServiceKind { get; set; } = string.Empty;
        public ulong SettingsVersion { get; set; } = 1;

        public LineSettingsState Clone()
        {
            return new LineSettingsState
            {
                OriginHoldLimitMinutes = OriginHoldLimitMinutes,
                MaxStationDwellMinutes = MaxStationDwellMinutes,
                AllowedDepotId = AllowedDepotId ?? string.Empty,
                ConfiguredServiceKind = ConfiguredServiceKind ?? string.Empty,
                SettingsVersion = SettingsVersion
            };
        }

        public static LineSettingsState Default(ulong version)
        {
            return new LineSettingsState
            {
                SettingsVersion = version
            };
        }
    }
}
