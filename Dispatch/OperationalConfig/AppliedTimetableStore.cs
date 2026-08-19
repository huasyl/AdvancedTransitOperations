using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod
{
    public enum LineKeyMigrateResult
    {
        Migrated,
        MissingLegacy,
        TargetOccupied,
        Rejected,
        SameKey,
        ModeMismatch
    }

    public sealed class AppliedTimetableStore
    {
        private readonly Dictionary<LineKey, AppliedTimetableState> m_Lines =
            new Dictionary<LineKey, AppliedTimetableState>();
        private readonly Dictionary<LineKey, Dictionary<int, AppliedTimetableRow>> m_Rows =
            new Dictionary<LineKey, Dictionary<int, AppliedTimetableRow>>();
        private ulong m_Version = 1;
        private Action<LineKey> m_LineChanged;
        private Action m_AllChanged;

        public ulong Version => m_Version;

        internal void SetDirtyCallbacks(Action<LineKey> lineChanged, Action allChanged)
        {
            m_LineChanged = lineChanged;
            m_AllChanged = allChanged;
        }

        public bool IsManaged(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            return !key.IsEmpty
                && m_Lines.TryGetValue(key, out AppliedTimetableState state)
                && state != null
                && state.Managed;
        }

        public bool TryGetRuntimeSummary(LineKey lineKey, out bool managed, out string serviceKind)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out AppliedTimetableState state)
                && state != null)
            {
                managed = state.Managed;
                serviceKind = state.ServiceKind ?? string.Empty;
                return true;
            }

            managed = false;
            serviceKind = string.Empty;
            return false;
        }

        public bool IsManaged(string lineId, TransitMode mode)
        {
            return IsManaged(LineIdentityService.GetKey(lineId, mode));
        }

        public int[] GetDepartures(LineKey lineKey)
        {
            AppliedTimetableState state = Get(lineKey);
            return state?.DepartureMinutes ?? Array.Empty<int>();
        }

        public string GetKind(LineKey lineKey)
        {
            AppliedTimetableState state = Get(lineKey);
            return state?.ServiceKind ?? string.Empty;
        }

        public AppliedTimetableState Get(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty
                || !m_Lines.TryGetValue(key, out AppliedTimetableState state)
                || state == null)
            {
                return AppliedTimetableState.Empty();
            }

            return state.Clone();
        }

        public AppliedTimetableState Get(string lineId, TransitMode mode)
        {
            return Get(LineIdentityService.GetKey(lineId, mode));
        }

        public bool TryGet(LineKey lineKey, out AppliedTimetableState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (!key.IsEmpty
                && m_Lines.TryGetValue(key, out AppliedTimetableState stored)
                && stored != null)
            {
                state = stored.Clone();
                return true;
            }

            state = AppliedTimetableState.Empty();
            return false;
        }

        public bool TryGet(string lineId, TransitMode mode, out AppliedTimetableState state)
        {
            return TryGet(LineIdentityService.GetKey(lineId, mode), out state);
        }

        internal bool TryGetRow(
            LineKey lineKey,
            int departureMinute,
            out AppliedTimetableRow row,
            out string stopSig)
        {
            row = null;
            stopSig = string.Empty;
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty
                || !m_Lines.TryGetValue(key, out AppliedTimetableState state)
                || state == null
                || !m_Rows.TryGetValue(key, out Dictionary<int, AppliedTimetableRow> rows)
                || !rows.TryGetValue(departureMinute, out AppliedTimetableRow stored)
                || stored == null)
            {
                return false;
            }

            row = stored.Clone();
            stopSig = state.StopSig ?? string.Empty;
            return true;
        }

        public void Apply(LineKey lineKey, AppliedTimetableState state)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty)
                throw new ArgumentException("lineKey", nameof(lineKey));

            if (state == null)
            {
                if (m_Lines.Remove(key))
                {
                    m_Rows.Remove(key);
                    m_Version++;
                    m_LineChanged?.Invoke(key);
                }
                return;
            }

            AppliedTimetableState replacement = Normalize(state);
            m_Lines[key] = replacement;
            IndexRows(key, replacement);
            m_Version++;
            m_LineChanged?.Invoke(key);
        }

        public void Clear(LineKey lineKey)
        {
            LineKey key = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);
            if (key.IsEmpty)
                return;

            if (m_Lines.Remove(key))
            {
                m_Rows.Remove(key);
                m_Version++;
                m_LineChanged?.Invoke(key);
            }
        }

        public void Clear()
        {
            if (m_Lines.Count > 0)
            {
                m_Version++;
                m_AllChanged?.Invoke();
            }
            m_Lines.Clear();
            m_Rows.Clear();
        }

        public IEnumerable<KeyValuePair<LineKey, AppliedTimetableState>> GetAll()
        {
            foreach (KeyValuePair<LineKey, AppliedTimetableState> entry in m_Lines)
            {
                if (entry.Value == null)
                    continue;

                yield return new KeyValuePair<LineKey, AppliedTimetableState>(
                    entry.Key,
                    entry.Value.Clone());
            }
        }

        public IEnumerable<KeyValuePair<LineKey, AppliedTimetableState>> GetAll(TransitMode mode)
        {
            foreach (KeyValuePair<LineKey, AppliedTimetableState> entry in GetAll())
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
                || !m_Lines.TryGetValue(legacyKey, out AppliedTimetableState state)
                || state == null)
            {
                return false;
            }

            m_Lines[targetKey] = state.Clone();
            m_Lines.Remove(legacyKey);
            if (m_Rows.TryGetValue(legacyKey, out Dictionary<int, AppliedTimetableRow> rows))
            {
                m_Rows[targetKey] = rows;
                m_Rows.Remove(legacyKey);
            }
            m_Version++;
            m_AllChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Explicit one-shot move from a legacy numeric key (mode:n) to mode:guid32.
        /// Does not scan lines, read RouteNumber, or invent mappings.
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

            if (!m_Lines.TryGetValue(legacy, out AppliedTimetableState state) || state == null)
                return LineKeyMigrateResult.MissingLegacy;

            if (m_Lines.ContainsKey(stable))
                return LineKeyMigrateResult.TargetOccupied;

            m_Lines[stable] = state.Clone();
            m_Lines.Remove(legacy);
            if (m_Rows.TryGetValue(legacy, out Dictionary<int, AppliedTimetableRow> rows))
            {
                m_Rows[stable] = rows;
                m_Rows.Remove(legacy);
            }
            m_Version++;
            m_AllChanged?.Invoke();
            return LineKeyMigrateResult.Migrated;
        }

        private static AppliedTimetableState Normalize(AppliedTimetableState state)
        {
            AppliedTimetableRow[] rows = CloneRows(state.AppliedRows);
            int[] departures = RuntimeConfigStoreDefaults.NormalizeDepartures(state.DepartureMinutes);
            if (departures.Length == 0 && rows.Length > 0)
            {
                departures = RuntimeConfigStoreDefaults.NormalizeDepartures(
                    rows.Where(row => row != null && row.DepartureMinute >= 0)
                        .Select(row => row.DepartureMinute));
            }

            return new AppliedTimetableState
            {
                Managed = state.Managed,
                DepartureMinutes = departures,
                ServiceKind = NormalizeKind(state.ServiceKind, rows, departures),
                StopSig = state.StopSig ?? string.Empty,
                AppliedRows = rows
            };
        }

        private void IndexRows(LineKey key, AppliedTimetableState state)
        {
            Dictionary<int, AppliedTimetableRow> rows = new Dictionary<int, AppliedTimetableRow>();
            HashSet<int> duplicates = new HashSet<int>();
            AppliedTimetableRow[] appliedRows = state?.AppliedRows ?? Array.Empty<AppliedTimetableRow>();
            for (int i = 0; i < appliedRows.Length; i++)
            {
                AppliedTimetableRow row = appliedRows[i];
                if (row == null || row.DepartureMinute < 0 || duplicates.Contains(row.DepartureMinute))
                    continue;

                if (rows.ContainsKey(row.DepartureMinute))
                {
                    rows.Remove(row.DepartureMinute);
                    duplicates.Add(row.DepartureMinute);
                }
                else
                {
                    rows.Add(row.DepartureMinute, row);
                }
            }

            m_Rows[key] = rows;
        }

        private static AppliedTimetableRow[] CloneRows(IEnumerable<AppliedTimetableRow> rows)
        {
            if (rows == null)
                return Array.Empty<AppliedTimetableRow>();

            List<AppliedTimetableRow> clone = new List<AppliedTimetableRow>();
            foreach (AppliedTimetableRow row in rows)
            {
                if (row == null)
                    continue;

                clone.Add(row.Clone());
            }

            return clone.ToArray();
        }

        private static string NormalizeKind(
            string explicitKind,
            IReadOnlyList<AppliedTimetableRow> rows,
            IReadOnlyList<int> departures)
        {
            string normalizedExplicitKind = RuntimeConfigStoreDefaults.NormalizeAppliedServiceKind(explicitKind);
            if (!string.IsNullOrEmpty(normalizedExplicitKind))
                return normalizedExplicitKind;

            bool sawExpress = false;
            bool sawLocal = false;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    string kind = RuntimeConfigStoreDefaults.NormalizeAppliedServiceKind(rows[i]?.ServiceKind);
                    if (string.IsNullOrEmpty(kind))
                        continue;

                    if (string.Equals(kind, RuntimeConfigStoreDefaults.ExpressServiceKind, StringComparison.Ordinal))
                    {
                        sawExpress = true;
                    }
                    else
                    {
                        sawLocal = true;
                    }

                    if (sawExpress && sawLocal)
                        return RuntimeConfigStoreDefaults.LocalServiceKind;
                }
            }

            if (sawExpress)
                return RuntimeConfigStoreDefaults.ExpressServiceKind;

            if (sawLocal || (departures != null && departures.Count > 0))
                return RuntimeConfigStoreDefaults.LocalServiceKind;

            return string.Empty;
        }
    }

    public sealed class AppliedTimetableState
    {
        public bool Managed { get; set; }
        public int[] DepartureMinutes { get; set; } = Array.Empty<int>();
        public string ServiceKind { get; set; } = string.Empty;
        public string StopSig { get; set; } = string.Empty;
        public AppliedTimetableRow[] AppliedRows { get; set; } = Array.Empty<AppliedTimetableRow>();

        public AppliedTimetableState Clone()
        {
            return new AppliedTimetableState
            {
                Managed = Managed,
                DepartureMinutes = (DepartureMinutes ?? Array.Empty<int>()).ToArray(),
                ServiceKind = ServiceKind ?? string.Empty,
                StopSig = StopSig ?? string.Empty,
                AppliedRows = (AppliedRows ?? Array.Empty<AppliedTimetableRow>())
                    .Where(row => row != null)
                    .Select(row => row.Clone())
                    .ToArray()
            };
        }

        public static AppliedTimetableState Empty()
        {
            return new AppliedTimetableState();
        }
    }

    public sealed class AppliedTimetableRow
    {
        public string RowId { get; set; } = string.Empty;
        public int DepartureMinute { get; set; } = -1;
        public string ServiceKind { get; set; } = string.Empty;
        public string OriginKey { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public TimedStop[] TimedStops { get; set; } = Array.Empty<TimedStop>();

        public AppliedTimetableRow Clone()
        {
            return new AppliedTimetableRow
            {
                RowId = RowId ?? string.Empty,
                DepartureMinute = DepartureMinute,
                ServiceKind = RuntimeConfigStoreDefaults.NormalizeAppliedServiceKind(ServiceKind),
                OriginKey = OriginKey ?? string.Empty,
                Source = Source ?? string.Empty,
                TimedStops = (TimedStops ?? Array.Empty<TimedStop>())
                    .Where(stop => stop != null)
                    .Select(stop => stop.Clone())
                    .ToArray()
            };
        }
    }

    public sealed class TimedStop
    {
        public string StopKey { get; set; } = string.Empty;
        public int Arrive { get; set; } = -1;
        public int Depart { get; set; } = -1;

        public TimedStop Clone()
        {
            return new TimedStop
            {
                StopKey = StopKey ?? string.Empty,
                Arrive = Arrive,
                Depart = Depart
            };
        }
    }

    internal static class RuntimeConfigStoreDefaults
    {
        public const int DefaultOriginHoldLimitMinutes = 20;
        public const int DefaultMaxStationDwellMinutes = 10;
        public const int MinConfiguredMinutes = 5;
        public const int MaxConfiguredMinutes = 120;
        public const string LocalServiceKind = "local";
        public const string ExpressServiceKind = "express";

        public static LineKey NormalizeLineKey(LineKey lineKey)
        {
            return lineKey.IsEmpty
                ? LineKey.Empty
                : lineKey;
        }

        public static int[] NormalizeDepartures(IEnumerable<int> departureMinutes)
        {
            if (departureMinutes == null)
                return Array.Empty<int>();

            SortedSet<int> normalized = new SortedSet<int>();
            foreach (int minute in departureMinutes)
            {
                if (minute < 0 || minute >= 24 * 60)
                    continue;

                normalized.Add(minute);
            }

            return normalized.ToArray();
        }

        public static string NormalizeAppliedServiceKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                return string.Empty;

            return string.Equals(kind, ExpressServiceKind, StringComparison.Ordinal)
                ? ExpressServiceKind
                : LocalServiceKind;
        }

        public static string NormalizeConfiguredServiceKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                return string.Empty;

            return NormalizeAppliedServiceKind(kind);
        }

        public static string NormalizeAllowedDepotId(string depotId)
        {
            return depotId ?? string.Empty;
        }

        public static int Hold(int value)
        {
            if (value <= 0)
                return DefaultOriginHoldLimitMinutes;

            return Math.Max(MinConfiguredMinutes, Math.Min(MaxConfiguredMinutes, value));
        }

        public static int Dwell(int value)
        {
            if (value <= 0)
                return DefaultMaxStationDwellMinutes;

            return Math.Max(MinConfiguredMinutes, Math.Min(MaxConfiguredMinutes, value));
        }
    }
}
