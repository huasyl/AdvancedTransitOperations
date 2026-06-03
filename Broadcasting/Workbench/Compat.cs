namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal static class Compat
    {
        internal static void Build(Workbench workbench, DispatchWorkbenchPersistentState persisted)
        {
            workbench?.Persistence.Build(persisted);
        }

        internal static void Restore(Workbench workbench, DispatchWorkbenchPersistentState persisted)
        {
            workbench?.Persistence.Restore(persisted);
        }
    }
}
