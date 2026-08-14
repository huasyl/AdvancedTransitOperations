using System;
using Game;
using Game.SceneFlow;
using RapidTransitMod.Dispatch.Workbench;

namespace RapidTransitMod.Workbenches
{
    internal static class UiEvents
    {
        private const string Snap = "suhua::rt.workbench.onSnapshotChanged";
        private const string Catalog = "suhua::rt.workbench.onCatalog";
        private const string LineInvalidated = "suhua::rt.workbench.onLineInvalidated";
        private const string RunTimeQuery = "suhua::rt.workbench.onRunTimeQuery";
        private const string RunTimeInvalidated = "suhua::rt.workbench.onRunTimeInvalidated";
        private const string MonitorChanged = "suhua::rt.workbench.onMonitorChanged";
        private const string Broadcast = "suhua::rt.workbench.onBroadcastSnapshotChanged";
        private const string Asset = "suhua::rt.workbench.onBroadcastAssetPreviewStateChanged";
        private const string Rule = "suhua::rt.workbench.onBroadcastRulePreviewStateChanged";

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

        internal static void Push(DispatchWorkbenchLineInvalidationEvent payload)
        {
            string json = payload != null ? Json.Write(payload) : string.Empty;
            Push(LineInvalidated, json, "Workbench line invalidation event push failed: ");
        }

        internal static void Push(DispatchWorkbenchRunTimeQueryStatusDto payload)
        {
            string json = payload != null ? Json.Write(payload) : string.Empty;
            Push(RunTimeQuery, json, "Workbench run-time query event push failed: ");
        }

        internal static void Push(RunTimeInvalidationDto payload)
        {
            string json = payload != null ? Json.Write(payload) : string.Empty;
            Push(RunTimeInvalidated, json, "Workbench run-time invalidation event push failed: ");
        }

        internal static void Push(DispatchWorkbenchMonitorChangedDto payload)
        {
            string json = payload != null ? Json.Write(payload) : string.Empty;
            Push(MonitorChanged, json, "Workbench monitor event push failed: ");
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
