using System;
using System.Collections.Generic;
using cohtml.Net;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod.Workbenches
{
    internal static class Calls
    {
        private static readonly List<BoundEventHandle> Handles = new List<BoundEventHandle>();

        internal static bool Bind()
        {
            try
            {
                Unbind();
                return BindDispatch()
                    && BindHost()
                    && BindBroadcast()
                    && BindPassengerFlow()
                    && BindOverview()
                    && BindPlanner()
                    && BindLocale();
            }
            catch (Exception ex)
            {
                Mod.log.Info("DispatchWorkbench API binding failed: " + ex.GetType().Name + ": " + ex.Message);
                Unbind();
                return false;
            }
        }

        internal static void Unbind()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view != null)
            {
                for (int i = 0; i < Handles.Count; i++)
                {
                    try
                    {
                        view.UnbindCall(Handles[i]);
                    }
                    catch
                    {
                    }
                }
            }

            Handles.Clear();
        }

        private static void Bind(View view, string name, Delegate handler)
        {
            Handles.Add(view.BindCall(name, handler));
        }

        internal static string Load(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_PlannerApi?.Load(requestJson) ?? string.Empty;
        }

        internal static string Start(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_PlannerApi?.Start(requestJson) ?? string.Empty;
        }

        internal static string Status(string jobId)
        {
            return ModRuntimeHostSystem.Instance?.m_PlannerApi?.Status(jobId) ?? string.Empty;
        }

        internal static string Run(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_PlannerApi?.Run(requestJson) ?? string.Empty;
        }

        internal static string Observe()
        {
            return ModRuntimeHostSystem.Instance?.m_Observation.Json() ?? string.Empty;
        }

        internal static string Locale()
        {
            return GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
        }

        internal static string BuildFlavorJson()
        {
            return "{\"debugTools\":"
                + (BuildFlavor.DebugTools ? "true" : "false")
                + ",\"verboseLogs\":"
                + (BuildFlavor.VerboseLogs ? "true" : "false")
                + "}";
        }

        private static bool BindDispatch()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "loadSnapshot", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Load));
            Bind(view, ApiHost.Prefix + "refreshSnapshot", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Refresh));
            Bind(view, ApiHost.Prefix + "refreshMetadata", new Func<string, string>(global::RapidTransitMod.Workbenches.TransitCatalog.RefreshMetadata));
            Bind(view, ApiHost.Prefix + "refreshTransitCatalog", new Func<string, string>(global::RapidTransitMod.Workbenches.TransitCatalog.Refresh));
            Bind(view, ApiHost.Prefix + "saveWorkbenchDraft", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Legacy));
            Bind(view, ApiHost.Prefix + "saveNativeWorkbenchDraft", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Save));
            Bind(view, ApiHost.Prefix + "startNativeSaveOperation", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Start));
            Bind(view, ApiHost.Prefix + "getNativeSaveOperationStatus", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.Status));
            Bind(view, ApiHost.Prefix + "setWorkbenchHostState", new Func<string, string>(global::RapidTransitMod.Dispatch.Workbench.Api.HostState));
            return true;
        }

        private static bool BindHost()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "getBuildFlavor", new Func<string>(BuildFlavorJson));
            return true;
        }

        private static bool BindBroadcast()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "loadBroadcastSnapshot", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Load));
            Bind(view, ApiHost.Prefix + "refreshBroadcastSnapshot", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Refresh));
            Bind(view, ApiHost.Prefix + "loadBroadcastBindingSlotHints", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Hints));
            Bind(view, ApiHost.Prefix + "loadBroadcastAssetBrowser", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Browse));
            Bind(view, ApiHost.Prefix + "importBroadcastExternalAssets", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Import));
            Bind(view, ApiHost.Prefix + "deleteBroadcastAsset", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Delete));
            Bind(view, ApiHost.Prefix + "deleteAllBroadcastAssets", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.DeleteAll));
            Bind(view, ApiHost.Prefix + "saveBroadcastStationBinding", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveMap));
            Bind(view, ApiHost.Prefix + "saveBroadcastStationBindings", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveMaps));
            Bind(view, ApiHost.Prefix + "autoBindBroadcastStationMappings", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.AutoMap));
            Bind(view, ApiHost.Prefix + "saveBroadcastRules", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SaveRules));
            Bind(view, ApiHost.Prefix + "saveBroadcastPlatformAnnouncement", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.SavePlatform));
            Bind(view, ApiHost.Prefix + "copyBroadcastPlatformAnnouncementToAllStations", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.CopyPlatform));
            Bind(view, ApiHost.Prefix + "applyBroadcastConfig", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Apply));
            Bind(view, ApiHost.Prefix + "openBroadcastAssetDirectoryPicker", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Pick));
            Bind(view, ApiHost.Prefix + "playBroadcastAssetPreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Play));
            Bind(view, ApiHost.Prefix + "stopBroadcastAssetPreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Stop));
            Bind(view, ApiHost.Prefix + "playBroadcastRulePreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.PlayRule));
            Bind(view, ApiHost.Prefix + "stopBroadcastRulePreview", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.StopRule));
            Bind(view, ApiHost.Prefix + "setBroadcastPreviewVolume", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.Volume));
            Bind(view, ApiHost.Prefix + "startBroadcastApplyOperation", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.StartApply));
            Bind(view, ApiHost.Prefix + "getBroadcastApplyOperationStatus", new Func<string, string>(global::RapidTransitMod.Broadcasting.WorkbenchBackend.Api.ApplyStatus));
            return true;
        }

        private static bool BindPlanner()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "loadPlannerContext", new Func<string, string>(Load));
            Bind(view, ApiHost.Prefix + "exportPlannerInput", new Func<string, string>(Load));
            Bind(view, ApiHost.Prefix + "startPlannerJob", new Func<string, string>(Start));
            Bind(view, ApiHost.Prefix + "getPlannerJobStatus", new Func<string, string>(Status));
            Bind(view, ApiHost.Prefix + "runPlanner", new Func<string, string>(Run));
            Bind(view, ApiHost.Prefix + "getObservationSnapshot", new Func<string>(Observe));
            return true;
        }

        private static bool BindPassengerFlow()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "loadPassengerFlowSnapshot", new Func<string, string>(global::RapidTransitMod.PassengerFlow.Api.Load));
            return true;
        }

        private static bool BindOverview()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "startOverviewFeatureSettingsOperation", new Func<string, string>(global::RapidTransitMod.Overview.FeatureSettingsApi.Start));
            Bind(view, ApiHost.Prefix + "getOverviewFeatureSettingsOperationStatus", new Func<string, string>(global::RapidTransitMod.Overview.FeatureSettingsApi.Status));
            return true;
        }

        private static bool BindLocale()
        {
            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return false;
            }

            Bind(view, ApiHost.Prefix + "getLocale", new Func<string>(Locale));
            return true;
        }
    }
}
