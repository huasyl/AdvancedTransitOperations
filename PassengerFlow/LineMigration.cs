using System;

namespace RapidTransitMod.PassengerFlow
{
    internal static class LineMigration
    {
        internal static void Run(LineAnchorCatalog catalog, MigrationReport report)
        {
            if (catalog == null || report == null)
                return;

            State state = SamplingSystem.CurrentState;
            if (state == null)
                return;

            state.Aggregates.MigrateLineIds(catalog, report);
            MigrateLegacyStationVolumes(state, catalog, report);
            MigrateLegacySectionVolumes(state, catalog, report);
            MigrateLegacyOdFlows(state, catalog, report);
            MigrateLegacyWarnings(state, catalog, report);
        }

        private static void MigrateLegacyStationVolumes(
            State state, LineAnchorCatalog catalog, MigrationReport report)
        {
            PassengerFlowPersistedStationVolume[] rows = state.LegacyStationVolumes;
            if (rows == null || rows.Length == 0)
                return;

            const string domain = "passengerflow-legacy-station-volume";
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedStationVolume row = rows[i];
                if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                    continue;

                string promoted = Aggregates.PromoteLineId(row.lineId, domain, catalog, report, mode);
                if (string.Equals(promoted, row.lineId, StringComparison.Ordinal))
                    continue;

                Aggregates.RecordFieldMigration(domain, row.lineId, promoted, false, report, mode);
                row.lineId = promoted;
            }
        }

        private static void MigrateLegacySectionVolumes(
            State state, LineAnchorCatalog catalog, MigrationReport report)
        {
            PassengerFlowPersistedSectionVolume[] rows = state.LegacySectionVolumes;
            if (rows == null || rows.Length == 0)
                return;

            const string domain = "passengerflow-legacy-section-volume";
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedSectionVolume row = rows[i];
                if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                    continue;

                string promoted = Aggregates.PromoteLineId(row.lineId, domain, catalog, report, mode);
                if (string.Equals(promoted, row.lineId, StringComparison.Ordinal))
                    continue;

                Aggregates.RecordFieldMigration(domain, row.lineId, promoted, false, report, mode);
                row.lineId = promoted;
            }
        }

        private static void MigrateLegacyOdFlows(
            State state, LineAnchorCatalog catalog, MigrationReport report)
        {
            PassengerFlowPersistedOdFlow[] rows = state.LegacyOdFlows;
            if (rows == null || rows.Length == 0)
                return;

            const string domain = "passengerflow-legacy-od-flow";
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedOdFlow row = rows[i];
                if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                    continue;

                string promotedFirst = Aggregates.PromoteLineId(row.firstLineId, domain, catalog, report, mode);
                if (!string.Equals(promotedFirst, row.firstLineId, StringComparison.Ordinal))
                {
                    Aggregates.RecordFieldMigration(domain, row.firstLineId, promotedFirst, false, report, mode);
                    row.firstLineId = promotedFirst;
                }

                string promotedLast = Aggregates.PromoteLineId(row.lastLineId, domain, catalog, report, mode);
                if (!string.Equals(promotedLast, row.lastLineId, StringComparison.Ordinal))
                {
                    Aggregates.RecordFieldMigration(domain, row.lastLineId, promotedLast, false, report, mode);
                    row.lastLineId = promotedLast;
                }
            }
        }

        private static void MigrateLegacyWarnings(
            State state, LineAnchorCatalog catalog, MigrationReport report)
        {
            PassengerFlowPersistedWarning[] rows = state.LegacyWarnings;
            if (rows == null || rows.Length == 0)
                return;

            const string domain = "passengerflow-legacy-warning";
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedWarning row = rows[i];
                if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                    continue;

                string promoted = Aggregates.PromoteLineId(row.lineId, domain, catalog, report, mode);
                if (string.Equals(promoted, row.lineId, StringComparison.Ordinal))
                    continue;

                Aggregates.RecordFieldMigration(domain, row.lineId, promoted, false, report, mode);
                row.lineId = promoted;
            }
        }
    }
}
