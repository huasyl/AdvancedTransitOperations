using System;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod.Workbenches
{
    internal static class UiEvents
    {
        private const string Snap = "huasyl::rt.workbench.onSnapshotChanged";
        private const string Catalog = "huasyl::rt.workbench.onCatalog";
        private const string Broadcast = "huasyl::rt.workbench.onBroadcastSnapshotChanged";
        private const string Asset = "huasyl::rt.workbench.onBroadcastAssetPreviewStateChanged";
        private const string Rule = "huasyl::rt.workbench.onBroadcastRulePreviewStateChanged";

        internal static void Push(DispatchWorkbenchSnapshot snapshot)
        {
            PushJson(snapshot != null ? Json.Write(snapshot) : string.Empty);
        }

        internal static void PushJson(string snapshotJson)
        {
            string payload = snapshotJson ?? string.Empty;
            Push(Snap, payload, "Workbench snapshot event push failed: ");
        }

        internal static void Push(DispatchWorkbenchCatalogEvent payload)
        {
            string json = payload != null ? Json.Write(payload) : string.Empty;
            Push(Catalog, json, "Workbench catalog event push failed: ");
        }

        internal static void Push(BroadcastWorkbenchSnapshot snapshot)
        {
            string payload = snapshot != null ? Json.Write(snapshot) : string.Empty;
            Push(Broadcast, payload, "Broadcast snapshot event push failed: ");
        }

        internal static void Push(BroadcastWorkbenchAssetPreviewStateDto state)
        {
            string payload = state != null ? Json.Write(state) : string.Empty;
            Push(Asset, payload, "Broadcast asset preview event push failed: ");
        }

        internal static void Push(BroadcastWorkbenchRulePreviewStateDto state)
        {
            string payload = state != null ? Json.Write(state) : string.Empty;
            Push(Rule, payload, "Broadcast rule preview event push failed: ");
        }

        private static void Push(string eventName, string payload, string errorPrefix)
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
