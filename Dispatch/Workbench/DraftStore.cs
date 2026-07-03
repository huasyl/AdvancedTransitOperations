using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class DraftStore : IEnumerable<KeyValuePair<string, DispatchWorkbenchDraftState>>
    {
        private readonly Dictionary<string, DispatchWorkbenchDraftState> m_Drafts =
            new Dictionary<string, DispatchWorkbenchDraftState>(StringComparer.Ordinal);
        private readonly Dictionary<TransitMode, string> m_PreferredLineIdsByMode =
            new Dictionary<TransitMode, string>();
        private string m_PreferredLineId = string.Empty;

        public int Count => m_Drafts.Count;

        public Dictionary<string, DispatchWorkbenchDraftState>.KeyCollection Keys => m_Drafts.Keys;

        public Dictionary<string, DispatchWorkbenchDraftState>.ValueCollection Values => m_Drafts.Values;

        public DispatchWorkbenchDraftState this[string lineKey]
        {
            get => m_Drafts[lineKey];
            set => m_Drafts[lineKey] = value;
        }

        public static string GetKey(string lineId)
        {
            return string.IsNullOrEmpty(lineId) ? "__default__" : lineId;
        }

        public static string GetKey(LineKey lineKey)
        {
            return GetKey(LineIdentityService.GetId(lineKey));
        }

        public static string GetKey(string lineId, TransitMode mode)
        {
            return GetKey(LineIdentityService.NormalizeForMode(lineId, mode));
        }

        public void Clear()
        {
            m_Drafts.Clear();
            m_PreferredLineIdsByMode.Clear();
            m_PreferredLineId = string.Empty;
        }

        public bool TryGetValue(string lineKey, out DispatchWorkbenchDraftState draft)
        {
            return m_Drafts.TryGetValue(lineKey, out draft);
        }

        public string GetPreferredLineId()
        {
            return m_PreferredLineId;
        }

        public string GetPreferredLineId(TransitMode mode)
        {
            if (mode != TransitMode.Unknown
                && m_PreferredLineIdsByMode.TryGetValue(mode, out string scopedLineId)
                && !string.IsNullOrEmpty(scopedLineId))
            {
                return scopedLineId;
            }

            return MatchesMode(m_PreferredLineId, mode)
                ? m_PreferredLineId
                : string.Empty;
        }

        public string ResolvePreferredLineId()
        {
            if (!string.IsNullOrEmpty(m_PreferredLineId))
                return m_PreferredLineId;
            if (m_Drafts.Count == 0)
                return string.Empty;

            KeyValuePair<string, DispatchWorkbenchDraftState> first = m_Drafts.First();
            return first.Value?.SelectedLineId ?? string.Empty;
        }

        public string ResolvePreferredLineId(TransitMode mode)
        {
            if (mode == TransitMode.Unknown)
                return ResolvePreferredLineId();

            string preferredLineId = GetPreferredLineId(mode);
            if (!string.IsNullOrEmpty(preferredLineId))
                return LineIdentityService.NormalizeForMode(preferredLineId, mode);

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string candidate = !string.IsNullOrEmpty(entry.Value?.SelectedLineId)
                    ? entry.Value.SelectedLineId
                    : entry.Key;
                if (MatchesMode(candidate, mode))
                    return LineIdentityService.NormalizeForMode(candidate, mode);
            }

            return string.Empty;
        }

        public void SetPreferredLineId(string lineId)
        {
            m_PreferredLineId = lineId ?? string.Empty;
            LineKey key = LineIdentityService.GetKey(m_PreferredLineId);
            if (key.HasMode)
            {
                m_PreferredLineIdsByMode[key.Mode] = m_PreferredLineId;
            }
        }

        public void SetPreferredLineId(string lineId, TransitMode mode)
        {
            string normalized = mode == TransitMode.Unknown
                ? lineId ?? string.Empty
                : LineIdentityService.NormalizeForMode(lineId, mode);
            m_PreferredLineId = normalized;
            if (mode != TransitMode.Unknown && !string.IsNullOrEmpty(normalized))
            {
                m_PreferredLineIdsByMode[mode] = normalized;
            }
        }

        public void SetPreferredLineId(LineKey lineKey)
        {
            SetPreferredLineId(LineIdentityService.GetId(lineKey));
        }

        public IEnumerable<KeyValuePair<TransitMode, string>> GetPreferredLineIdsByMode()
        {
            return m_PreferredLineIdsByMode
                .Where(entry => !string.IsNullOrEmpty(entry.Value))
                .OrderBy(entry => TransitModeCodec.Format(entry.Key), StringComparer.Ordinal);
        }

        public void SetPreferredLineId(TransitMode mode, string lineId)
        {
            if (mode == TransitMode.Unknown || string.IsNullOrEmpty(lineId))
                return;

            m_PreferredLineIdsByMode[mode] = LineIdentityService.NormalizeForMode(lineId, mode);
        }

        private static bool MatchesMode(string lineId, TransitMode mode)
        {
            return mode == TransitMode.Unknown || new ModeScope(mode).MatchesLineId(lineId);
        }

        public IEnumerator<KeyValuePair<string, DispatchWorkbenchDraftState>> GetEnumerator()
        {
            return m_Drafts.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
