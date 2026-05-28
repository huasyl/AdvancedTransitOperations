namespace RapidTransitMod
{
    internal static class DispatchWorkbenchEuisBridge
    {
        internal static void Initialize(string modRootPath)
        {
            WorkbenchApi.Initialize(modRootPath);
        }

        internal static void NotifyWorkbenchSnapshotChanged(DispatchWorkbenchSnapshot snapshot)
        {
            WorkbenchEvents.PublishSnapshot(snapshot);
        }

        internal static void NotifyBroadcastWorkbenchSnapshotChanged(BroadcastWorkbenchSnapshot snapshot)
        {
            WorkbenchEvents.PublishBroadcastSnapshot(snapshot);
        }

        internal static void NotifyBroadcastAssetPreviewStateChanged(BroadcastWorkbenchAssetPreviewStateDto state)
        {
            WorkbenchEvents.PublishAssetPreview(state);
        }

        internal static void NotifyBroadcastRulePreviewStateChanged(BroadcastWorkbenchRulePreviewStateDto state)
        {
            WorkbenchEvents.PublishRulePreview(state);
        }
    }
}
