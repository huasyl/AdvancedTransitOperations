using System;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod.Workbenches
{
    internal static class Calls
    {
        internal static bool Bind()
        {
            return BindDispatch()
                && BindBroadcast()
                && BindPassengerFlow()
                && BindOverview()
                && BindPlanner()
                && BindLocale();
        }

        internal static string Load(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_PlannerApi?.Load(requestJson) ?? string.Empty;
        }

        internal static string Start(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_PlannerApi?.Start(requestJson) ?? string.Empty;
        }

        internal static string Status(string jobId)
        {
            return DispatchRuntimeSystem.Instance?.m_PlannerApi?.Status(jobId) ?? string.Empty;
        }

        internal static string Run(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_PlannerApi?.Run(requestJson) ?? string.Empty;
        }

        internal static string Observe()
        {
            return DispatchRuntimeSystem.Instance?.m_Observation.Json() ?? string.Empty;
        }

        internal static string Locale()
        {
            return GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
        }

        private static bool BindDispatch()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "loadSnapshot", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Load));
            view.BindCall(ApiHost.Prefix + "refreshSnapshot", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Refresh));
            view.BindCall(ApiHost.Prefix + "refreshMetadata", new Func<string, string>(global::RapidTransitMod.Workbenches.TransitCatalog.RefreshMetadata));
            view.BindCall(ApiHost.Prefix + "refreshTransitCatalog", new Func<string, string>(global::RapidTransitMod.Workbenches.TransitCatalog.Refresh));
            view.BindCall(ApiHost.Prefix + "saveWorkbenchDraft", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Legacy));
            view.BindCall(ApiHost.Prefix + "saveNativeWorkbenchDraft", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Save));
            view.BindCall(ApiHost.Prefix + "startNativeSaveOperation", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Start));
            view.BindCall(ApiHost.Prefix + "getNativeSaveOperationStatus", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Status));
            view.BindCall(ApiHost.Prefix + "setWorkbenchHostState", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.HostState));
            return true;
        }

        private static bool BindBroadcast()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "loadBroadcastSnapshot", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Load));
            view.BindCall(ApiHost.Prefix + "refreshBroadcastSnapshot", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Refresh));
            view.BindCall(ApiHost.Prefix + "loadBroadcastBindingSlotHints", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Hints));
            view.BindCall(ApiHost.Prefix + "loadBroadcastAssetBrowser", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Browse));
            view.BindCall(ApiHost.Prefix + "importBroadcastExternalAssets", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Import));
            view.BindCall(ApiHost.Prefix + "deleteBroadcastAsset", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Delete));
            view.BindCall(ApiHost.Prefix + "deleteAllBroadcastAssets", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.DeleteAll));
            view.BindCall(ApiHost.Prefix + "saveBroadcastStationBinding", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveMap));
            view.BindCall(ApiHost.Prefix + "saveBroadcastStationBindings", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveMaps));
            view.BindCall(ApiHost.Prefix + "autoBindBroadcastStationMappings", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.AutoMap));
            view.BindCall(ApiHost.Prefix + "saveBroadcastRules", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveRules));
            view.BindCall(ApiHost.Prefix + "saveBroadcastPlatformAnnouncement", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SavePlatform));
            view.BindCall(ApiHost.Prefix + "copyBroadcastPlatformAnnouncementToAllStations", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.CopyPlatform));
            view.BindCall(ApiHost.Prefix + "applyBroadcastConfig", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Apply));
            view.BindCall(ApiHost.Prefix + "openBroadcastAssetDirectoryPicker", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Pick));
            view.BindCall(ApiHost.Prefix + "playBroadcastAssetPreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Play));
            view.BindCall(ApiHost.Prefix + "stopBroadcastAssetPreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Stop));
            view.BindCall(ApiHost.Prefix + "playBroadcastRulePreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.PlayRule));
            view.BindCall(ApiHost.Prefix + "stopBroadcastRulePreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.StopRule));
            view.BindCall(ApiHost.Prefix + "setBroadcastPreviewVolume", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Volume));
            view.BindCall(ApiHost.Prefix + "startBroadcastApplyOperation", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.StartApply));
            view.BindCall(ApiHost.Prefix + "getBroadcastApplyOperationStatus", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.ApplyStatus));
            return true;
        }

        private static bool BindPlanner()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "loadPlannerContext", new Func<string, string>(Load));
            view.BindCall(ApiHost.Prefix + "exportPlannerInput", new Func<string, string>(Load));
            view.BindCall(ApiHost.Prefix + "startPlannerJob", new Func<string, string>(Start));
            view.BindCall(ApiHost.Prefix + "getPlannerJobStatus", new Func<string, string>(Status));
            view.BindCall(ApiHost.Prefix + "runPlanner", new Func<string, string>(Run));
            view.BindCall(ApiHost.Prefix + "getObservationSnapshot", new Func<string>(Observe));
            return true;
        }

        private static bool BindPassengerFlow()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "loadPassengerFlowSnapshot", new Func<string, string>(global::RapidTransitMod.PassengerFlow.Api.Load));
            return true;
        }

        private static bool BindOverview()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "startOverviewFeatureSettingsOperation", new Func<string, string>(global::RapidTransitMod.Overview.FeatureSettingsApi.Start));
            view.BindCall(ApiHost.Prefix + "getOverviewFeatureSettingsOperationStatus", new Func<string, string>(global::RapidTransitMod.Overview.FeatureSettingsApi.Status));
            return true;
        }

        private static bool BindLocale()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            view.BindCall(ApiHost.Prefix + "getLocale", new Func<string>(Locale));
            return true;
        }
    }
}
