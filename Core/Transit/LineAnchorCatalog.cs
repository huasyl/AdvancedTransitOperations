using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Common;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    /// <summary>
    /// Main-thread sole write owner for <see cref="Lak"/>.
    /// Memory indexes are rebuilt by explicit <see cref="Scan"/>; only entity Lak is persisted.
    /// RouteNumber indexes exist only to promote old mode:number data during the compatibility window.
    /// Normal runtime identity and business storage use the Lak-backed stable key exclusively.
    /// </summary>
    internal sealed class LineAnchorCatalog
    {
        internal const string ReasonInvalid = "invalid-lak";
        internal const string ReasonDuplicate = "duplicate-lak";

        private readonly EntityManager m_EntityManager;
        private readonly Dictionary<Entity, LineKey> m_StableByEntity =
            new Dictionary<Entity, LineKey>();
        private readonly Dictionary<Entity, string> m_IsolationByEntity =
            new Dictionary<Entity, string>();
        private readonly Dictionary<LineKey, Entity> m_EntityByStable =
            new Dictionary<LineKey, Entity>();
        private readonly Dictionary<LineKey, LineKey> m_LegacyToStable =
            new Dictionary<LineKey, LineKey>();
        private readonly HashSet<LineKey> m_LegacyConflict =
            new HashSet<LineKey>();

        private bool m_Changed;
        private bool m_HasIsolation;

        internal LineAnchorCatalog(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        /// <summary>True when the last <see cref="Scan"/> changed indexes or wrote Lak.</summary>
        internal bool Changed => m_Changed;

        /// <summary>True when any live line is isolated from the stable identity chain.</summary>
        internal bool HasIsolation => m_HasIsolation;

        /// <summary>Count of isolated live lines after the last scan.</summary>
        internal int IsolationCount => m_IsolationByEntity.Count;

        internal void ClearChanged()
        {
            m_Changed = false;
        }

        /// <summary>
        /// Full re-entrant scan. Caller supplies the current live line set.
        /// Only ATO dispatch-eligible lines are processed. Writes Lak only for missing or empty
        /// values (and Created-only duplicate regen). Unsupported modes are skipped entirely.
        /// </summary>
        /// <returns>True if ECS writes or in-memory indexes changed.</returns>
        internal bool Scan(IEnumerable<Entity> lines)
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            List<Entry> entries = new List<Entry>();
            HashSet<Entity> seen = new HashSet<Entity>();
            foreach (Entity line in lines)
            {
                if (line == Entity.Null
                    || !m_EntityManager.Exists(line)
                    || !seen.Add(line))
                {
                    continue;
                }

                if (!DispatchLineEligibility.IsDispatchTransportLine(m_EntityManager, line))
                    continue;

                Entry entry = ReadEntry(line);
                if (entry.Mode == TransitMode.Unknown)
                    continue;

                entries.Add(entry);
            }

            bool wrote = false;
            int createdSummaryCount = 0;
            int regeneratedSummaryCount = 0;
            HashSet<string> usedGuids = new HashSet<string>(StringComparer.Ordinal);

            // Collect every existing valid GUID before minting any new ones.
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.RawValue.Length == 0)
                {
                    entry.ValidGuid = null;
                    entry.Invalid = false;
                    entries[i] = entry;
                    continue;
                }

                if (IsGuid32(entry.RawValue))
                {
                    entry.ValidGuid = entry.RawValue;
                    entry.Invalid = false;
                    usedGuids.Add(entry.RawValue);
                    entries[i] = entry;
                    continue;
                }

                entry.ValidGuid = null;
                entry.Invalid = true;
                entries[i] = entry;
            }

            // Mint only for missing/empty Lak. New values avoid all occupied legal GUIDs.
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.Invalid || entry.ValidGuid != null || entry.RawValue.Length != 0)
                    continue;

                string guid = NewGuid(usedGuids);
                WriteLak(entry.Entity, guid);
                entry.RawValue = guid;
                entry.ValidGuid = guid;
                entry.Invalid = false;
                entries[i] = entry;
                if (RtLog.VerboseEnabled)
                    LogAnchor(entry, guid, "created", entry.HasLak ? "empty-lak" : "missing-lak");
                createdSummaryCount++;
                wrote = true;
            }

            Dictionary<string, List<int>> byGuid = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                string guid = entries[i].ValidGuid;
                if (guid == null)
                    continue;

                List<int> group;
                if (!byGuid.TryGetValue(guid, out group))
                {
                    group = new List<int>();
                    byGuid[guid] = group;
                }

                group.Add(i);
            }

            HashSet<int> duplicateIsolated = new HashSet<int>();
            foreach (KeyValuePair<string, List<int>> pair in byGuid)
            {
                List<int> group = pair.Value;
                if (group.Count <= 1)
                    continue;

                int createdIndex = -1;
                int createdCount = 0;
                for (int g = 0; g < group.Count; g++)
                {
                    int idx = group[g];
                    if (!entries[idx].IsCreated)
                        continue;

                    createdCount++;
                    createdIndex = idx;
                }

                if (createdCount == 1)
                {
                    // Keep original GUID occupied in usedGuids; only mint a new one for Created.
                    Entry created = entries[createdIndex];
                    string duplicateGuid = created.ValidGuid;
                    string guid = NewGuid(usedGuids);
                    WriteLak(created.Entity, guid);
                    created.RawValue = guid;
                    created.ValidGuid = guid;
                    created.Invalid = false;
                    entries[createdIndex] = created;
                    if (RtLog.VerboseEnabled)
                        LogAnchor(created, guid, "regenerated", "created-duplicate-lak", duplicateGuid);
                    regeneratedSummaryCount++;
                    wrote = true;

                    int remainingCount = 0;
                    for (int g = 0; g < group.Count; g++)
                    {
                        if (group[g] != createdIndex)
                            remainingCount++;
                    }

                    if (remainingCount > 1)
                    {
                        for (int g = 0; g < group.Count; g++)
                        {
                            int idx = group[g];
                            if (idx != createdIndex)
                                duplicateIsolated.Add(idx);
                        }
                    }

                    continue;
                }

                for (int g = 0; g < group.Count; g++)
                    duplicateIsolated.Add(group[g]);
            }

            // Pass 1: legacy groups include every eligible line with a valid RouteNumber,
            // including lines that will be identity-isolated.
            Dictionary<LineKey, List<Entity>> legacyGroups = new Dictionary<LineKey, List<Entity>>();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.Mode == TransitMode.Unknown || !IsValidRouteNumber(entry.RouteNumber))
                    continue;

                LineKey legacy = new LineKey(
                    entry.Mode,
                    entry.RouteNumber.ToString(CultureInfo.InvariantCulture));
                List<Entity> group;
                if (!legacyGroups.TryGetValue(legacy, out group))
                {
                    group = new List<Entity>();
                    legacyGroups[legacy] = group;
                }

                group.Add(entry.Entity);
            }

            // Pass 2: stable identity indexes and isolation reasons.
            Dictionary<Entity, LineKey> stableByEntity = new Dictionary<Entity, LineKey>();
            Dictionary<Entity, string> isolationByEntity = new Dictionary<Entity, string>();
            Dictionary<LineKey, Entity> entityByStable = new Dictionary<LineKey, Entity>();

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.Mode == TransitMode.Unknown)
                    continue;

                if (entry.Invalid)
                {
                    isolationByEntity[entry.Entity] = ReasonInvalid;
                    LogIsolation(entry, ReasonInvalid);
                    continue;
                }

                if (duplicateIsolated.Contains(i))
                {
                    isolationByEntity[entry.Entity] = ReasonDuplicate;
                    LogIsolation(entry, ReasonDuplicate);
                    continue;
                }

                if (entry.ValidGuid == null)
                {
                    isolationByEntity[entry.Entity] = ReasonInvalid;
                    LogIsolation(entry, ReasonInvalid);
                    continue;
                }

                LineKey stable = new LineKey(entry.Mode, entry.ValidGuid);
                stableByEntity[entry.Entity] = stable;
                entityByStable[stable] = entry.Entity;
            }

            // Pass 3: unique + non-isolated + stable only; any multi-hit is conflict.
            Dictionary<LineKey, LineKey> legacyToStable = new Dictionary<LineKey, LineKey>();
            HashSet<LineKey> legacyConflict = new HashSet<LineKey>();
            foreach (KeyValuePair<LineKey, List<Entity>> pair in legacyGroups)
            {
                List<Entity> group = pair.Value;
                if (group.Count > 1)
                {
                    legacyConflict.Add(pair.Key);
                    continue;
                }

                Entity only = group[0];
                LineKey stable;
                if (stableByEntity.TryGetValue(only, out stable))
                    legacyToStable[pair.Key] = stable;
            }

            bool indexesChanged = !MapsEqual(m_StableByEntity, stableByEntity)
                || !IsolationEqual(m_IsolationByEntity, isolationByEntity)
                || !EntityMapsEqual(m_EntityByStable, entityByStable)
                || !MapsEqual(m_LegacyToStable, legacyToStable)
                || !SetEqual(m_LegacyConflict, legacyConflict);

            m_StableByEntity.Clear();
            foreach (KeyValuePair<Entity, LineKey> pair in stableByEntity)
                m_StableByEntity[pair.Key] = pair.Value;

            m_IsolationByEntity.Clear();
            foreach (KeyValuePair<Entity, string> pair in isolationByEntity)
                m_IsolationByEntity[pair.Key] = pair.Value;

            m_EntityByStable.Clear();
            foreach (KeyValuePair<LineKey, Entity> pair in entityByStable)
                m_EntityByStable[pair.Key] = pair.Value;

            m_LegacyToStable.Clear();
            foreach (KeyValuePair<LineKey, LineKey> pair in legacyToStable)
                m_LegacyToStable[pair.Key] = pair.Value;

            m_LegacyConflict.Clear();
            foreach (LineKey key in legacyConflict)
                m_LegacyConflict.Add(key);

            m_HasIsolation = m_IsolationByEntity.Count > 0;
            m_Changed = wrote || indexesChanged;
            if (createdSummaryCount > 0 || regeneratedSummaryCount > 0)
            {
                Mod.log.Info("[LineAnchorMigration] summary: created=" + createdSummaryCount
                    + " regenerated=" + regeneratedSummaryCount);
            }
            return m_Changed;
        }

        /// <summary>Entity → stable LineKey (mode:guid32). Empty when missing or isolated.</summary>
        internal LineKey StableKey(Entity line)
        {
            LineKey key;
            if (line == Entity.Null || !m_StableByEntity.TryGetValue(line, out key))
                return LineKey.Empty;

            return key;
        }

        /// <summary>Entity → isolation reason, or empty when not isolated.</summary>
        internal string Isolation(Entity line)
        {
            string reason;
            if (line == Entity.Null || !m_IsolationByEntity.TryGetValue(line, out reason))
                return string.Empty;

            return reason ?? string.Empty;
        }

        /// <summary>Stable LineKey → Entity. Read-only; never writes ECS.</summary>
        internal bool TryEntity(LineKey stable, out Entity entity)
        {
            if (stable.IsEmpty)
            {
                entity = Entity.Null;
                return false;
            }

            return m_EntityByStable.TryGetValue(stable, out entity);
        }

        /// <summary>
        /// Legacy mode:number → unique stable key.
        /// False when missing, isolated-only, or when RouteNumber collides across lines.
        /// </summary>
        internal bool TryLegacy(LineKey legacy, out LineKey stable)
        {
            stable = LineKey.Empty;
            if (legacy.IsEmpty || m_LegacyConflict.Contains(legacy))
                return false;

            return m_LegacyToStable.TryGetValue(legacy, out stable);
        }

        /// <summary>True when multiple live lines share the same valid RouteNumber for a mode.</summary>
        internal bool IsLegacyConflict(LineKey legacy)
        {
            return !legacy.IsEmpty && m_LegacyConflict.Contains(legacy);
        }

        private Entry ReadEntry(Entity line)
        {
            Entry entry = new Entry
            {
                Entity = line,
                RawValue = string.Empty,
                ValidGuid = null,
                Invalid = false,
                HasLak = m_EntityManager.HasComponent<Lak>(line),
                IsCreated = m_EntityManager.HasComponent<Created>(line),
                Mode = TransportModeResolver.Resolve(m_EntityManager, line),
                RouteNumber = int.MaxValue
            };

            if (entry.HasLak)
            {
                entry.RawValue = m_EntityManager.GetComponentData<Lak>(line).Value.ToString()
                    ?? string.Empty;
            }

            if (m_EntityManager.HasComponent<RouteNumber>(line))
                entry.RouteNumber = m_EntityManager.GetComponentData<RouteNumber>(line).m_Number;

            return entry;
        }

        private void LogIsolation(Entry entry, string reason)
        {
            if (m_IsolationByEntity.TryGetValue(entry.Entity, out string previous)
                && string.Equals(previous, reason, StringComparison.Ordinal))
            {
                return;
            }

            LogAnchor(entry, string.Empty, "failed", reason, entry.RawValue);
        }

        private static void LogAnchor(
            Entry entry,
            string newGuid,
            string result,
            string reason,
            string currentLak = "")
        {
            string routeNumber = IsValidRouteNumber(entry.RouteNumber)
                ? entry.RouteNumber.ToString(CultureInfo.InvariantCulture)
                : "-";
            Mod.log.Info("[LineAnchorMigration] mode=" + TransitModeCodec.Format(entry.Mode)
                + " routeNumber=" + routeNumber
                + " newGuid=" + (string.IsNullOrEmpty(newGuid) ? "-" : newGuid)
                + " currentLak=" + (string.IsNullOrEmpty(currentLak) ? "-" : currentLak)
                + " result=" + result
                + " reason=" + reason
                + " entity=" + entry.Entity.Index.ToString(CultureInfo.InvariantCulture)
                + ":" + entry.Entity.Version.ToString(CultureInfo.InvariantCulture));
        }

        private void WriteLak(Entity line, string guid)
        {
            Lak lak = new Lak
            {
                Value = guid
            };

            if (m_EntityManager.HasComponent<Lak>(line))
                m_EntityManager.SetComponentData(line, lak);
            else
                m_EntityManager.AddComponentData(line, lak);
        }

        private static string NewGuid(HashSet<string> used)
        {
            string guid;
            do
            {
                guid = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(guid));

            return guid;
        }

        private static bool IsGuid32(string value)
        {
            if (value == null || value.Length != 32)
                return false;

            for (int i = 0; i < 32; i++)
            {
                char c = value[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                    continue;

                return false;
            }

            return true;
        }

        private static bool IsValidRouteNumber(int routeNumber)
        {
            return routeNumber >= 0 && routeNumber != int.MaxValue;
        }

        private static bool MapsEqual(
            Dictionary<Entity, LineKey> left,
            Dictionary<Entity, LineKey> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (KeyValuePair<Entity, LineKey> pair in left)
            {
                LineKey value;
                if (!right.TryGetValue(pair.Key, out value) || !pair.Value.Equals(value))
                    return false;
            }

            return true;
        }

        private static bool MapsEqual(
            Dictionary<LineKey, LineKey> left,
            Dictionary<LineKey, LineKey> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (KeyValuePair<LineKey, LineKey> pair in left)
            {
                LineKey value;
                if (!right.TryGetValue(pair.Key, out value) || !pair.Value.Equals(value))
                    return false;
            }

            return true;
        }

        private static bool EntityMapsEqual(
            Dictionary<LineKey, Entity> left,
            Dictionary<LineKey, Entity> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (KeyValuePair<LineKey, Entity> pair in left)
            {
                Entity value;
                if (!right.TryGetValue(pair.Key, out value) || pair.Value != value)
                    return false;
            }

            return true;
        }

        private static bool IsolationEqual(
            Dictionary<Entity, string> left,
            Dictionary<Entity, string> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (KeyValuePair<Entity, string> pair in left)
            {
                string value;
                if (!right.TryGetValue(pair.Key, out value)
                    || !string.Equals(pair.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SetEqual(HashSet<LineKey> left, HashSet<LineKey> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (LineKey key in left)
            {
                if (!right.Contains(key))
                    return false;
            }

            return true;
        }

        private struct Entry
        {
            public Entity Entity;
            public string RawValue;
            public string ValidGuid;
            public bool Invalid;
            public bool HasLak;
            public bool IsCreated;
            public TransitMode Mode;
            public int RouteNumber;
        }
    }
}
