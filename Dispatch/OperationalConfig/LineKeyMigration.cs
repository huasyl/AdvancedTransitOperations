using System;
using System.Collections.Generic;

namespace RapidTransitMod
{
    internal static class LineKeyMigration
    {
        internal static MigrationReport MigrateStores(
            LineAnchorCatalog catalog,
            AppliedTimetableStore appliedStore,
            LineConfigStore lineStore)
        {
            MigrationReport report = new MigrationReport();
            if (catalog == null)
                return report;

            if (appliedStore != null)
                MigrateAppliedStore(catalog, appliedStore, report);

            if (lineStore != null)
                MigrateLineConfigStore(catalog, lineStore, report);

            return report;
        }

        internal static void RunDomainMigrations(LineAnchorCatalog catalog, MigrationReport report)
        {
            if (catalog == null || report == null)
                return;

            RunDomain("workbench", () => global::RapidTransitMod.Dispatch.Workbench.LineMigration.Run(catalog, report));
            RunDomain("broadcasting", () => global::RapidTransitMod.Broadcasting.LineMigration.Run(catalog, report));
            RunDomain("passengerflow", () => global::RapidTransitMod.PassengerFlow.LineMigration.Run(catalog, report));
            RunDomain("planner", () => global::RapidTransitMod.Planner.LineMigration.Run(catalog, report));
        }

        private static void MigrateAppliedStore(
            LineAnchorCatalog catalog,
            AppliedTimetableStore store,
            MigrationReport report)
        {
            List<LineKey> legacyKeys = SnapshotLegacyKeys(store.GetAll());
            for (int i = 0; i < legacyKeys.Count; i++)
            {
                LineKey legacy = legacyKeys[i];
                if (catalog.IsLegacyConflict(legacy))
                {
                    report.Record("applied", legacy, LineKey.Empty, MigrationResult.LegacyConflict, "legacy RouteNumber conflict; orphan retained");
                    continue;
                }

                if (!catalog.TryLegacy(legacy, out LineKey stable))
                {
                    report.Record("applied", legacy, LineKey.Empty, MigrationResult.ZeroMatch, "no stable mapping; orphan retained");
                    continue;
                }

                LineKeyMigrateResult result = store.Migrate(legacy, stable);
                MapRecord(report, "applied", legacy, stable, result);
            }
        }

        private static void MigrateLineConfigStore(
            LineAnchorCatalog catalog,
            LineConfigStore store,
            MigrationReport report)
        {
            List<LineKey> legacyKeys = SnapshotLegacyKeys(store.GetAll());
            for (int i = 0; i < legacyKeys.Count; i++)
            {
                LineKey legacy = legacyKeys[i];
                if (catalog.IsLegacyConflict(legacy))
                {
                    report.Record("line-config", legacy, LineKey.Empty, MigrationResult.LegacyConflict, "legacy RouteNumber conflict; orphan retained");
                    continue;
                }

                if (!catalog.TryLegacy(legacy, out LineKey stable))
                {
                    report.Record("line-config", legacy, LineKey.Empty, MigrationResult.ZeroMatch, "no stable mapping; orphan retained");
                    continue;
                }

                LineKeyMigrateResult result = store.Migrate(legacy, stable);
                MapRecord(report, "line-config", legacy, stable, result);
            }
        }

        private static List<LineKey> SnapshotLegacyKeys<TValue>(
            IEnumerable<KeyValuePair<LineKey, TValue>> entries)
        {
            List<LineKey> legacyKeys = new List<LineKey>();
            foreach (KeyValuePair<LineKey, TValue> entry in entries)
            {
                if (LineKey.IsLegacyNumericKey(entry.Key))
                    legacyKeys.Add(entry.Key);
            }

            return legacyKeys;
        }

        private static void MapRecord(
            MigrationReport report,
            string domain,
            LineKey legacy,
            LineKey stable,
            LineKeyMigrateResult result)
        {
            switch (result)
            {
                case LineKeyMigrateResult.Migrated:
                    report.Record(domain, legacy, stable, MigrationResult.Migrated);
                    break;
                case LineKeyMigrateResult.MissingLegacy:
                    report.Record(domain, legacy, stable, MigrationResult.MissingLegacy);
                    break;
                case LineKeyMigrateResult.TargetOccupied:
                    report.Record(domain, legacy, stable, MigrationResult.TargetOccupied);
                    break;
                case LineKeyMigrateResult.ModeMismatch:
                    report.Record(domain, legacy, stable, MigrationResult.ModeMismatch);
                    break;
                case LineKeyMigrateResult.SameKey:
                    break;
                case LineKeyMigrateResult.Rejected:
                    report.Record(domain, legacy, stable, MigrationResult.ZeroMatch, "Migrate rejected");
                    break;
            }
        }

        private static void RunDomain(string domain, Action run)
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                Mod.log.Info("[LineKeyMigration] domain=" + domain + " failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
