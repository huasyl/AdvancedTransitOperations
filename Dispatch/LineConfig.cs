using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace RapidTransitMod.Dispatch
{
    internal sealed class LineConfig
    {
        private readonly LineConfigStore m_Store;
        private readonly Func<string, LineKey> m_KeyById;
        private readonly Func<Entity, string, LineKey> m_KeyByLine;
        private readonly Func<LineKey, string> m_IdByKey;
        private readonly Func<int, int> m_NormHold;
        private readonly Func<int, int> m_NormDwell;
        private readonly Func<string, string> m_NormDepot;
        private readonly Func<string, string> m_NormKind;

        internal LineConfig(
            LineConfigStore store,
            Func<string, LineKey> keyById,
            Func<Entity, string, LineKey> keyByLine,
            Func<LineKey, string> idByKey,
            Func<int, int> normHold,
            Func<int, int> normDwell,
            Func<string, string> normDepot,
            Func<string, string> normKind)
        {
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_KeyById = keyById ?? throw new ArgumentNullException(nameof(keyById));
            m_KeyByLine = keyByLine ?? throw new ArgumentNullException(nameof(keyByLine));
            m_IdByKey = idByKey ?? throw new ArgumentNullException(nameof(idByKey));
            m_NormHold = normHold ?? throw new ArgumentNullException(nameof(normHold));
            m_NormDwell = normDwell ?? throw new ArgumentNullException(nameof(normDwell));
            m_NormDepot = normDepot ?? throw new ArgumentNullException(nameof(normDepot));
            m_NormKind = normKind ?? throw new ArgumentNullException(nameof(normKind));
        }

        internal ulong Version => m_Store.Version;

        internal void Clear()
        {
            m_Store.Clear();
        }

        internal void Apply(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            if (settings == null)
                return;

            m_Store.Clear();
            foreach (DispatchWorkbenchLineSettingDto setting in settings)
            {
                if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    continue;

                LineKey key = Key(setting.lineId);
                if (key.IsEmpty)
                    continue;

                m_Store.Set(key, new LineConfigState
                {
                    OriginHoldLimitMinutes = m_NormHold(setting.originHoldLimitMinutes),
                    MaxStationDwellMinutes = m_NormDwell(setting.maxStationDwellMinutes),
                    AllowedDepotId = m_NormDepot(setting.allowedDepotId),
                    ConfiguredServiceKind = m_NormKind(setting.serviceKind)
                });
            }
        }

        internal void Apply(TransitMode mode, IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            if (mode == TransitMode.Unknown)
            {
                Apply(settings);
                return;
            }

            m_Store.Clear(mode);
            foreach (DispatchWorkbenchLineSettingDto setting in settings ?? Array.Empty<DispatchWorkbenchLineSettingDto>())
            {
                if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    continue;

                LineKey key = LineIdentityService.GetKey(setting.lineId, mode);
                if (key.IsEmpty || key.Mode != mode)
                    continue;

                m_Store.Set(key, new LineConfigState
                {
                    OriginHoldLimitMinutes = m_NormHold(setting.originHoldLimitMinutes),
                    MaxStationDwellMinutes = m_NormDwell(setting.maxStationDwellMinutes),
                    AllowedDepotId = m_NormDepot(setting.allowedDepotId),
                    ConfiguredServiceKind = m_NormKind(setting.serviceKind)
                });
            }
        }

        internal bool Same(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            Dictionary<string, DispatchWorkbenchLineSettingDto> requested =
                new Dictionary<string, DispatchWorkbenchLineSettingDto>(StringComparer.Ordinal);
            if (settings != null)
            {
                foreach (DispatchWorkbenchLineSettingDto setting in settings)
                {
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                        continue;

                    requested[setting.lineId] = setting;
                }
            }

            string[] lineIds = Keys().ToArray();
            if (requested.Count != lineIds.Length)
                return false;

            foreach (string lineId in lineIds)
            {
                if (!requested.TryGetValue(lineId, out DispatchWorkbenchLineSettingDto setting))
                    return false;

                if (m_NormHold(setting.originHoldLimitMinutes) != GetHold(lineId)
                    || m_NormDwell(setting.maxStationDwellMinutes) != GetDwell(lineId)
                    || !string.Equals(CompareDepotId(setting.allowedDepotId), CompareDepotId(GetDepotId(lineId)), StringComparison.Ordinal)
                    || !string.Equals(m_NormKind(setting.serviceKind), GetKind(lineId), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool Same(TransitMode mode, IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            if (mode == TransitMode.Unknown)
                return Same(settings);

            Dictionary<string, DispatchWorkbenchLineSettingDto> requested =
                new Dictionary<string, DispatchWorkbenchLineSettingDto>(StringComparer.Ordinal);
            if (settings != null)
            {
                foreach (DispatchWorkbenchLineSettingDto setting in settings)
                {
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                        continue;

                    string lineId = LineIdentityService.NormalizeForMode(setting.lineId, mode);
                    requested[lineId] = setting;
                }
            }

            string[] lineIds = Keys(mode).ToArray();
            if (requested.Count != lineIds.Length)
                return false;

            foreach (string lineId in lineIds)
            {
                if (!requested.TryGetValue(lineId, out DispatchWorkbenchLineSettingDto setting))
                    return false;

                if (m_NormHold(setting.originHoldLimitMinutes) != GetHold(lineId)
                    || m_NormDwell(setting.maxStationDwellMinutes) != GetDwell(lineId)
                    || !string.Equals(CompareDepotId(setting.allowedDepotId), CompareDepotId(GetDepotId(lineId)), StringComparison.Ordinal)
                    || !string.Equals(m_NormKind(setting.serviceKind), GetKind(lineId), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal IEnumerable<string> Keys()
        {
            return m_Store.GetAll()
                .Select(entry => m_IdByKey(entry.Key))
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal);
        }

        internal IEnumerable<string> Keys(TransitMode mode)
        {
            return m_Store.GetAll(mode)
                .Select(entry => m_IdByKey(entry.Key))
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal);
        }

        internal int GetHold(string lineId)
        {
            return State(lineId).OriginHoldLimitMinutes;
        }

        internal int GetHold(Entity line)
        {
            return State(Key(line, string.Empty)).OriginHoldLimitMinutes;
        }

        internal int GetDwell(string lineId)
        {
            return State(lineId).MaxStationDwellMinutes;
        }

        internal int GetDwell(Entity line)
        {
            return State(Key(line, string.Empty)).MaxStationDwellMinutes;
        }

        internal string GetDepotId(string lineId)
        {
            return State(lineId).AllowedDepotId ?? string.Empty;
        }

        internal string GetDepotId(Entity line)
        {
            return State(Key(line, string.Empty)).AllowedDepotId ?? string.Empty;
        }

        internal string GetKind(string lineId)
        {
            return m_NormKind(State(lineId).ConfiguredServiceKind);
        }

        internal string GetKind(Entity line)
        {
            return m_NormKind(State(Key(line, string.Empty)).ConfiguredServiceKind);
        }

        internal LineKey Key(string lineId)
        {
            return m_KeyById(lineId);
        }

        internal LineKey Key(Entity line, string lineId)
        {
            return m_KeyByLine(line, lineId);
        }

        private LineConfigState State(string lineId)
        {
            return State(Key(lineId));
        }

        private LineConfigState State(LineKey key)
        {
            return !key.IsEmpty && m_Store.TryGet(key, out LineConfigState state)
                ? state
                : LineConfigState.Default(m_Store.Version);
        }

        private static string CompareDepotId(string depotId)
        {
            return string.IsNullOrWhiteSpace(depotId) ? string.Empty : depotId;
        }
    }
}
