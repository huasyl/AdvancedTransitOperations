using System;
using System.Collections.Generic;
using System.Text;

namespace RapidTransitMod
{
    internal enum MigrationResult
    {
        Migrated,
        MissingLegacy,
        TargetOccupied,
        ZeroMatch,
        LegacyConflict,
        InvalidLak,
        DuplicateLak,
        ModeMismatch,
        EntityMissing
    }

    internal readonly struct MigrationEntry
    {
        public readonly string Domain;
        public readonly LineKey LegacyKey;
        public readonly LineKey StableKey;
        public readonly MigrationResult Result;
        public readonly string Reason;

        internal MigrationEntry(
            string domain,
            LineKey legacyKey,
            LineKey stableKey,
            MigrationResult result,
            string reason)
        {
            Domain = domain;
            LegacyKey = legacyKey;
            StableKey = stableKey;
            Result = result;
            Reason = reason ?? string.Empty;
        }
    }

    internal sealed class MigrationReport
    {
        private readonly List<MigrationEntry> m_Entries = new List<MigrationEntry>();
        private readonly HashSet<string> m_Seen = new HashSet<string>(System.StringComparer.Ordinal);

        internal void Record(
            string domain,
            LineKey legacyKey,
            LineKey stableKey,
            MigrationResult result,
            string reason = "")
        {
            string dedupe = domain
                + "|" + legacyKey.ToString()
                + "|" + result.ToString();
            if (!m_Seen.Add(dedupe))
                return;

            m_Entries.Add(new MigrationEntry(domain, legacyKey, stableKey, result, reason));
        }

        internal IReadOnlyList<MigrationEntry> Entries => m_Entries;

        internal int Count => m_Entries.Count;

        internal string Summary()
        {
            Dictionary<string, Dictionary<MigrationResult, int>> byDomain =
                new Dictionary<string, Dictionary<MigrationResult, int>>(System.StringComparer.Ordinal);
            for (int i = 0; i < m_Entries.Count; i++)
            {
                MigrationEntry entry = m_Entries[i];
                if (!byDomain.TryGetValue(entry.Domain ?? string.Empty, out Dictionary<MigrationResult, int> bucket))
                {
                    bucket = new Dictionary<MigrationResult, int>();
                    byDomain[entry.Domain ?? string.Empty] = bucket;
                }

                bucket[entry.Result] = (bucket.TryGetValue(entry.Result, out int c) ? c : 0) + 1;
            }

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, Dictionary<MigrationResult, int>> pair in byDomain)
            {
                if (sb.Length > 0)
                    sb.Append("; ");

                sb.Append(pair.Key).Append(": ");
                bool first = true;
                foreach (KeyValuePair<MigrationResult, int> r in pair.Value)
                {
                    if (!first)
                        sb.Append(", ");
                    first = false;
                    sb.Append(r.Key.ToString()).Append('=').Append(r.Value.ToString());
                }
            }

            return sb.ToString();
        }

        internal void LogDetails(Action<string> log)
        {
            if (log == null)
                return;

            for (int i = 0; i < m_Entries.Count; i++)
            {
                MigrationEntry entry = m_Entries[i];
                TransitMode mode = entry.LegacyKey.Mode != TransitMode.Unknown
                    ? entry.LegacyKey.Mode
                    : entry.StableKey.Mode;
                string routeNumber = LineKey.IsLegacyNumericKey(entry.LegacyKey)
                    ? entry.LegacyKey.Id
                    : "-";
                string newGuid = LineKey.IsStableGuidKey(entry.StableKey)
                    ? entry.StableKey.Id
                    : "-";
                string reason = string.IsNullOrEmpty(entry.Reason)
                    ? ResultReason(entry.Result)
                    : entry.Reason;

                log("[LineKeyMigration] domain=" + (entry.Domain ?? string.Empty)
                    + " mode=" + TransitModeCodec.Format(mode)
                    + " routeNumber=" + routeNumber
                    + " newGuid=" + newGuid
                    + " result=" + entry.Result.ToString()
                    + " reason=" + reason);
            }
        }

        private static string ResultReason(MigrationResult result)
        {
            switch (result)
            {
                case MigrationResult.Migrated:
                    return "ok";
                case MigrationResult.MissingLegacy:
                    return "legacy-key-missing";
                case MigrationResult.TargetOccupied:
                    return "stable-target-occupied";
                case MigrationResult.ZeroMatch:
                    return "no-live-line-match";
                case MigrationResult.LegacyConflict:
                    return "duplicate-route-number";
                case MigrationResult.InvalidLak:
                    return "invalid-lak";
                case MigrationResult.DuplicateLak:
                    return "duplicate-lak";
                case MigrationResult.ModeMismatch:
                    return "mode-mismatch";
                case MigrationResult.EntityMissing:
                    return "entity-missing";
                default:
                    return "unknown";
            }
        }

        internal void Clear()
        {
            m_Entries.Clear();
            m_Seen.Clear();
        }
    }
}
