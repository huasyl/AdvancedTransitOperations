using System;
using System.Collections.Generic;
using System.Linq;
using RapidTransitMod.Broadcasting.WorkbenchBackend;

namespace RapidTransitMod.Broadcasting
{
    internal static class LineMigration
    {
        private static State s_State;

        internal static void Attach(State state)
        {
            s_State = state;
        }

        internal static void Run(LineAnchorCatalog catalog, MigrationReport report)
        {
            State state = s_State;
            if (catalog == null || report == null || state == null)
                return;

            MigrateBindingDict(state.AppliedBindings, catalog, report);
            MigrateBindingDict(state.DraftBindings, catalog, report);
            MigrateRuleDict(state.AppliedRules, catalog, report);
            MigrateRuleDict(state.DraftRules, catalog, report);
            MigratePlatformDict(state.AppliedPlatforms, catalog, report);
            MigratePlatformDict(state.DraftPlatforms, catalog, report);
            MigrateAppliedLines(state.AppliedLines, catalog, report);
        }

        private static string PromoteKey(
            string lineId,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return lineId;

            LineKey key = LineIdentityService.GetKey(lineId);
            if (key.IsEmpty || LineKey.IsStableGuidKey(key))
                return lineId;

            if (!LineKey.IsLegacyNumericKey(key))
                return lineId;

            if (catalog.IsLegacyConflict(key))
            {
                report.Record(domain, key, LineKey.Empty, MigrationResult.LegacyConflict);
                return lineId;
            }

            if (catalog.TryLegacy(key, out LineKey stable))
                return LineIdentityService.GetId(stable);

            report.Record(domain, key, LineKey.Empty, MigrationResult.ZeroMatch);
            return lineId;
        }

        private static void RecordMigrated(
            string domain,
            string oldId,
            string newId,
            MigrationReport report)
        {
            report.Record(
                domain,
                LineIdentityService.GetKey(oldId),
                LineIdentityService.GetKey(newId),
                MigrationResult.Migrated);
        }

        private static void RecordOccupied(
            string domain,
            string oldId,
            string newId,
            MigrationReport report)
        {
            report.Record(
                domain,
                LineIdentityService.GetKey(oldId),
                LineIdentityService.GetKey(newId),
                MigrationResult.TargetOccupied);
        }

        private static void MigrateBindingDict(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (source == null || source.Count == 0)
                return;

            List<KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>> snapshot =
                source.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string oldKey = snapshot[i].Key;
                string newKey = PromoteKey(oldKey, "broadcasting-bindings", catalog, report);
                if (string.Equals(newKey, oldKey, StringComparison.Ordinal))
                    continue;

                if (source.ContainsKey(newKey))
                {
                    RecordOccupied("broadcasting-bindings", oldKey, newKey, report);
                    continue;
                }

                RecordMigrated("broadcasting-bindings", oldKey, newKey, report);
                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> value = snapshot[i].Value;
                source.Remove(oldKey);
                source[newKey] = value;
            }
        }

        private static void MigrateRuleDict(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (source == null || source.Count == 0)
                return;

            List<KeyValuePair<string, List<BroadcastWorkbenchRuleDto>>> snapshot = source.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string oldKey = snapshot[i].Key;
                string newKey = PromoteKey(oldKey, "broadcasting-rules", catalog, report);
                if (string.Equals(newKey, oldKey, StringComparison.Ordinal))
                    continue;

                if (source.ContainsKey(newKey))
                {
                    RecordOccupied("broadcasting-rules", oldKey, newKey, report);
                    continue;
                }

                RecordMigrated("broadcasting-rules", oldKey, newKey, report);
                List<BroadcastWorkbenchRuleDto> value = snapshot[i].Value;
                source.Remove(oldKey);
                source[newKey] = value;
            }
        }

        private static void MigratePlatformDict(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (source == null || source.Count == 0)
                return;

            List<KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>> snapshot =
                source.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string oldKey = snapshot[i].Key;
                string newKey = PromoteKey(oldKey, "broadcasting-platforms", catalog, report);
                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> value = snapshot[i].Value;

                if (!string.Equals(newKey, oldKey, StringComparison.Ordinal))
                {
                    if (source.ContainsKey(newKey))
                    {
                        RecordOccupied("broadcasting-platforms", oldKey, newKey, report);
                        continue;
                    }

                    RecordMigrated("broadcasting-platforms", oldKey, newKey, report);
                    source.Remove(oldKey);
                    source[newKey] = value;
                }

                if (value == null || value.Count == 0)
                    continue;

                foreach (BroadcastWorkbenchPlatformAnnouncementDto announcement in value.Values)
                {
                    if (announcement == null)
                        continue;

                    string oldLineId = announcement.lineId;
                    if (string.IsNullOrWhiteSpace(oldLineId))
                        continue;

                    string newLineId = PromoteKey(oldLineId, "broadcasting-platforms", catalog, report);
                    if (string.Equals(newLineId, oldLineId, StringComparison.Ordinal))
                        continue;

                    RecordMigrated("broadcasting-platforms", oldLineId, newLineId, report);
                    announcement.lineId = newLineId;
                }
            }
        }

        private static void MigrateAppliedLines(
            HashSet<string> appliedLines,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (appliedLines == null || appliedLines.Count == 0)
                return;

            List<string> snapshot = appliedLines.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string oldId = snapshot[i];
                string newId = PromoteKey(oldId, "broadcasting-applied", catalog, report);
                if (string.IsNullOrEmpty(newId)
                    || string.Equals(newId, oldId, StringComparison.Ordinal))
                    continue;

                if (appliedLines.Contains(newId))
                {
                    RecordOccupied("broadcasting-applied", oldId, newId, report);
                    continue;
                }

                RecordMigrated("broadcasting-applied", oldId, newId, report);
                appliedLines.Remove(oldId);
                appliedLines.Add(newId);
            }
        }
    }
}
