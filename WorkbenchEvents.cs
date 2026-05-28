using System;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod
{
    internal static class WorkbenchEvents
    {
        private const string SnapshotChangedEvent = "suhua::rt.workbench.onSnapshotChanged";
        private const string BroadcastSnapshotChangedEvent = "suhua::rt.workbench.onBroadcastSnapshotChanged";
        private const string BroadcastAssetPreviewStateChangedEvent = "suhua::rt.workbench.onBroadcastAssetPreviewStateChanged";
        private const string BroadcastRulePreviewStateChangedEvent = "suhua::rt.workbench.onBroadcastRulePreviewStateChanged";

        internal static void PublishSnapshot(DispatchWorkbenchSnapshot snapshot)
        {
            PublishSnapshotJson(snapshot != null ? DispatchWorkbenchJson.Serialize(snapshot) : string.Empty);
        }

        internal static void PublishSnapshotJson(string snapshotJson)
        {
            string payload = snapshotJson ?? string.Empty;
            DispatchWorkbenchUISystem.PublishSnapshotJson(payload);
            Publish(SnapshotChangedEvent, payload, "Workbench snapshot event push failed: ");
        }

        internal static void PublishBroadcastSnapshot(BroadcastWorkbenchSnapshot snapshot)
        {
            string payload = snapshot != null ? DispatchWorkbenchJson.Serialize(snapshot) : string.Empty;
            Publish(BroadcastSnapshotChangedEvent, payload, "Broadcast snapshot event push failed: ");
        }

        internal static void PublishAssetPreview(BroadcastWorkbenchAssetPreviewStateDto state)
        {
            string payload = state != null ? DispatchWorkbenchJson.Serialize(state) : string.Empty;
            Publish(BroadcastAssetPreviewStateChangedEvent, payload, "Broadcast asset preview event push failed: ");
        }

        internal static void PublishRulePreview(BroadcastWorkbenchRulePreviewStateDto state)
        {
            string payload = state != null ? DispatchWorkbenchJson.Serialize(state) : string.Empty;
            Publish(BroadcastRulePreviewStateChangedEvent, payload, "Broadcast rule preview event push failed: ");
        }

        internal static void PublishSaveResult(string resultJson)
        {
            DispatchWorkbenchUISystem.PublishSaveResultJson(resultJson ?? string.Empty);
        }

        private static void Publish(string eventName, string payload, string errorPrefix)
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return;
            }

            try
            {
                if (view.IsReadyForBindings())
                {
                    view.TriggerEvent<string>(eventName, payload ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info(errorPrefix + ex.Message);
            }
        }
    }
}
