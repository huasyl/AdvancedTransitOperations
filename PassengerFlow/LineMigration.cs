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
        }
    }
}
