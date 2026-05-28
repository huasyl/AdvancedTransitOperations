using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod
{
    internal sealed class WorkbenchDraftStore : IEnumerable<KeyValuePair<string, DispatchWorkbenchDraftState>>
    {
        private readonly Dictionary<string, DispatchWorkbenchDraftState> m_Drafts =
            new Dictionary<string, DispatchWorkbenchDraftState>(StringComparer.Ordinal);
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
            return LineIdentityService.NormalizeForMode(ResolvePreferredLineId(), mode);
        }

        public void SetPreferredLineId(string lineId)
        {
            m_PreferredLineId = lineId ?? string.Empty;
        }

        public void SetPreferredLineId(LineKey lineKey)
        {
            SetPreferredLineId(LineIdentityService.GetId(lineKey));
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
