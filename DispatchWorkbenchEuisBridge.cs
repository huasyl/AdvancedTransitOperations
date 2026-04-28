using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.SceneFlow;

namespace RapidTransitMod
{
    internal static class DispatchWorkbenchEuisBridge
    {
        private const string HostKey = "rapidtransitmod";
        private const string EuisModder = "suhua";
        private const string EuisAcronym = "rt";
        private const string AppId = "dispatch-workbench";
        private const string SnapshotChangedEvent = EuisModder + "::" + EuisAcronym + ".workbench.onSnapshotChanged";
        private const string BroadcastSnapshotChangedEvent = EuisModder + "::" + EuisAcronym + ".workbench.onBroadcastSnapshotChanged";
        private const string BroadcastAssetPreviewStateChangedEvent = EuisModder + "::" + EuisAcronym + ".workbench.onBroadcastAssetPreviewStateChanged";
        private const string BroadcastRulePreviewStateChangedEvent = EuisModder + "::" + EuisAcronym + ".workbench.onBroadcastRulePreviewStateChanged";
        private const string DevServerConfigFile = "dispatch-workbench-euis-dev-url.txt";
        private const string LegacyReadonlyMessage = "Legacy EUIS workbench is now read-only. Use the native Schedule panel to edit and apply timetables.";

        private static bool s_IsInitialized;
        private static bool s_IsRegistered;
        private static bool s_HostRegistered;
        private static bool s_EuisAppRegistered;
        private static bool s_EuisDeferredLogged;
        private static DateTime s_NextEuisAssetLookupUtc;
        private static string s_ModRootPath = string.Empty;
        private static Action<string, object[]> s_EuisCaller;

        internal static string ModRootPath => s_ModRootPath;

        internal static void Initialize(string modRootPath)
        {
            if (s_IsInitialized || string.IsNullOrEmpty(modRootPath))
            {
                return;
            }

            s_IsInitialized = true;
            s_ModRootPath = modRootPath;
            MainThreadDispatcher.RegisterUpdater(RegisterOnce);
        }

        internal static void NotifyWorkbenchSnapshotChanged(DepartureControlSystem.DispatchWorkbenchSnapshot snapshot)
        {
            string snapshotJson = snapshot != null ? DispatchWorkbenchJson.Serialize(snapshot) : string.Empty;
            DispatchWorkbenchUISystem.PublishSnapshotJson(snapshotJson);

            if (!s_IsRegistered || GameManager.instance?.userInterface?.view?.View == null)
            {
                return;
            }

            try
            {
                if (GameManager.instance.userInterface.view.View.IsReadyForBindings())
                {
                    GameManager.instance.userInterface.view.View.TriggerEvent<string>(SnapshotChangedEvent, snapshotJson);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("Workbench snapshot event push failed: " + ex.Message);
            }

            try
            {
                s_EuisCaller?.Invoke("workbench.onSnapshotChanged", new object[] { snapshotJson });
            }
            catch (Exception ex)
            {
                Mod.log.Info("Workbench EUIS callback push failed: " + ex.Message);
            }
        }

        internal static void NotifyBroadcastWorkbenchSnapshotChanged(DepartureControlSystem.BroadcastWorkbenchSnapshot snapshot)
        {
            string snapshotJson = snapshot != null ? DispatchWorkbenchJson.Serialize(snapshot) : string.Empty;

            if (!s_IsRegistered || GameManager.instance?.userInterface?.view?.View == null)
            {
                return;
            }

            try
            {
                if (GameManager.instance.userInterface.view.View.IsReadyForBindings())
                {
                    GameManager.instance.userInterface.view.View.TriggerEvent<string>(BroadcastSnapshotChangedEvent, snapshotJson);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast snapshot event push failed: " + ex.Message);
            }

            try
            {
                s_EuisCaller?.Invoke("workbench.onBroadcastSnapshotChanged", new object[] { snapshotJson });
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast EUIS callback push failed: " + ex.Message);
            }
        }

        internal static void NotifyBroadcastAssetPreviewStateChanged(DepartureControlSystem.BroadcastWorkbenchAssetPreviewStateDto state)
        {
            string stateJson = state != null ? DispatchWorkbenchJson.Serialize(state) : string.Empty;

            if (!s_IsRegistered || GameManager.instance?.userInterface?.view?.View == null)
            {
                return;
            }

            try
            {
                if (GameManager.instance.userInterface.view.View.IsReadyForBindings())
                {
                    GameManager.instance.userInterface.view.View.TriggerEvent<string>(BroadcastAssetPreviewStateChangedEvent, stateJson);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast asset preview event push failed: " + ex.Message);
            }

            try
            {
                s_EuisCaller?.Invoke("workbench.onBroadcastAssetPreviewStateChanged", new object[] { stateJson });
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast asset preview callback push failed: " + ex.Message);
            }
        }

        internal static void NotifyBroadcastRulePreviewStateChanged(DepartureControlSystem.BroadcastWorkbenchRulePreviewStateDto state)
        {
            string stateJson = state != null ? DispatchWorkbenchJson.Serialize(state) : string.Empty;

            if (!s_IsRegistered || GameManager.instance?.userInterface?.view?.View == null)
            {
                return;
            }

            try
            {
                if (GameManager.instance.userInterface.view.View.IsReadyForBindings())
                {
                    GameManager.instance.userInterface.view.View.TriggerEvent<string>(BroadcastRulePreviewStateChangedEvent, stateJson);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast rule preview event push failed: " + ex.Message);
            }

            try
            {
                s_EuisCaller?.Invoke("workbench.onBroadcastRulePreviewStateChanged", new object[] { stateJson });
            }
            catch (Exception ex)
            {
                Mod.log.Info("Broadcast rule preview callback push failed: " + ex.Message);
            }
        }

        private static bool RegisterOnce()
        {
            if (s_IsRegistered)
            {
                return true;
            }

            try
            {
                if (GameManager.instance?.userInterface?.view?.uiSystem == null || string.IsNullOrEmpty(s_ModRootPath))
                {
                    return false;
                }

                if (!s_HostRegistered)
                {
                    // Keep EUIS registration in two stages.
                    // Host location and backend calls can be registered as soon as the UI system exists,
                    // but RegisterAppForEUIS must stay deferred on the main-thread updater.
                    // Collapsing this back into a single immediate registration path caused the EUIS entry
                    // to disappear even though the mod UI module itself was already registered.
                    GameManager.instance.userInterface.view.uiSystem.AddHostLocation(
                        HostKey,
                        new HashSet<(string, int)> { (s_ModRootPath, 0) },
                        true);
                    RegisterBackendCalls();
                    MainThreadDispatcher.RegisterUpdater(RegisterAtEuisWhenReady);
                    s_HostRegistered = true;
                    Mod.log.Info("DispatchWorkbench host location registered.");
                }

                s_IsRegistered = true;
                return true;
            }
            catch (Exception ex)
            {
                Mod.log.Info("DispatchWorkbench EUIS bridge registration failed: " + ex.Message);
                return false;
            }
        }

        private static void RegisterBackendCalls()
        {
            var view = GameManager.instance.userInterface.view.View;
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.loadSnapshot", new Func<string>(HandleLoadSnapshot));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.refreshSnapshot", new Func<string>(HandleRefreshSnapshot));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.loadBroadcastSnapshot", new Func<string, string>(HandleLoadBroadcastSnapshot));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.refreshBroadcastSnapshot", new Func<string, string>(HandleRefreshBroadcastSnapshot));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.loadBroadcastBindingSlotHints", new Func<string, string>(HandleLoadBroadcastBindingSlotHints));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.loadBroadcastAssetBrowser", new Func<string, string>(HandleLoadBroadcastAssetBrowser));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.importBroadcastExternalAssets", new Func<string, string>(HandleImportBroadcastExternalAssets));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.deleteBroadcastAsset", new Func<string, string>(HandleDeleteBroadcastAsset));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.deleteAllBroadcastAssets", new Func<string>(HandleDeleteAllBroadcastAssets));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveBroadcastStationBinding", new Func<string, string>(HandleSaveBroadcastStationBinding));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveBroadcastStationBindings", new Func<string, string>(HandleSaveBroadcastStationBindings));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.autoBindBroadcastStationMappings", new Func<string, string>(HandleAutoBindBroadcastStationMappings));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveBroadcastRules", new Func<string, string>(HandleSaveBroadcastRules));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveBroadcastPlatformAnnouncement", new Func<string, string>(HandleSaveBroadcastPlatformAnnouncement));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.copyBroadcastPlatformAnnouncementToAllStations", new Func<string, string>(HandleCopyBroadcastPlatformAnnouncementToAllStations));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.applyBroadcastConfig", new Func<string, string>(HandleApplyBroadcastConfig));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.openBroadcastAssetDirectoryPicker", new Func<string>(HandleOpenBroadcastAssetDirectoryPicker));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.playBroadcastAssetPreview", new Func<string, string>(HandlePlayBroadcastAssetPreview));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.stopBroadcastAssetPreview", new Func<string, string>(HandleStopBroadcastAssetPreview));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.playBroadcastRulePreview", new Func<string, string>(HandlePlayBroadcastRulePreview));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.stopBroadcastRulePreview", new Func<string, string>(HandleStopBroadcastRulePreview));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.setBroadcastPreviewVolume", new Func<string, string>(HandleSetBroadcastPreviewVolume));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.refreshMetadata", new Func<string>(HandleRefreshMetadata));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveWorkbenchDraft", new Func<string, string>(HandleSaveDraft));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveNativeWorkbenchDraft", new Func<string, string>(HandleSaveNativeDraft));
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.getLocale", new Func<string>(HandleGetLocale));
        }

        private static string HandleLoadSnapshot()
        {
            string snapshotJson = DepartureControlSystem.Instance?.LoadWorkbenchSnapshotJson() ?? string.Empty;
            DispatchWorkbenchUISystem.PublishSnapshotJson(snapshotJson);
            return snapshotJson;
        }

        private static string HandleRefreshSnapshot()
        {
            string snapshotJson = DepartureControlSystem.Instance?.RefreshWorkbenchSnapshotJson() ?? string.Empty;
            DispatchWorkbenchUISystem.PublishSnapshotJson(snapshotJson);
            return snapshotJson;
        }

        private static string HandleLoadBroadcastSnapshot(string preferredLineId)
        {
            return DepartureControlSystem.Instance?.LoadBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        private static string HandleRefreshBroadcastSnapshot(string preferredLineId)
        {
            return DepartureControlSystem.Instance?.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        private static string HandleLoadBroadcastBindingSlotHints(string lineId)
        {
            return DepartureControlSystem.Instance?.LoadBroadcastBindingSlotHintsJson(lineId) ?? string.Empty;
        }

        private static string HandleLoadBroadcastAssetBrowser(string requestedPath)
        {
            return DepartureControlSystem.Instance?.LoadBroadcastAssetBrowserJson(requestedPath) ?? string.Empty;
        }

        private static string HandleImportBroadcastExternalAssets(string requestJson)
        {
            return DepartureControlSystem.Instance?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;
        }

        private static string HandleDeleteBroadcastAsset(string assetName)
        {
            return DepartureControlSystem.Instance?.DeleteBroadcastAssetJson(assetName) ?? string.Empty;
        }

        private static string HandleDeleteAllBroadcastAssets()
        {
            return DepartureControlSystem.Instance?.DeleteAllBroadcastAssetsJson() ?? string.Empty;
        }

        private static string HandleSaveBroadcastStationBinding(string requestJson)
        {
            return DepartureControlSystem.Instance?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;
        }

        private static string HandleSaveBroadcastStationBindings(string requestJson)
        {
            return DepartureControlSystem.Instance?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;
        }

        private static string HandleAutoBindBroadcastStationMappings(string lineId)
        {
            return DepartureControlSystem.Instance?.AutoBindBroadcastStationMappingsJson(lineId) ?? string.Empty;
        }

        private static string HandleSaveBroadcastRules(string requestJson)
        {
            return DepartureControlSystem.Instance?.SaveBroadcastRulesJson(requestJson) ?? string.Empty;
        }

        private static string HandleSaveBroadcastPlatformAnnouncement(string requestJson)
        {
            return DepartureControlSystem.Instance?.SaveBroadcastPlatformAnnouncementJson(requestJson) ?? string.Empty;
        }

        private static string HandleCopyBroadcastPlatformAnnouncementToAllStations(string requestJson)
        {
            return DepartureControlSystem.Instance?.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson) ?? string.Empty;
        }

        private static string HandleApplyBroadcastConfig(string requestJson)
        {
            return DepartureControlSystem.Instance?.ApplyBroadcastConfigJson(requestJson) ?? string.Empty;
        }

        private static string HandleOpenBroadcastAssetDirectoryPicker()
        {
            return DepartureControlSystem.Instance?.OpenBroadcastAssetDirectoryPickerJson() ?? string.Empty;
        }

        private static string HandlePlayBroadcastAssetPreview(string assetName)
        {
            return DepartureControlSystem.Instance?.PlayBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        private static string HandlePlayBroadcastRulePreview(string requestJson)
        {
            return DepartureControlSystem.Instance?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;
        }

        private static string HandleStopBroadcastAssetPreview(string assetName)
        {
            return DepartureControlSystem.Instance?.StopBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        private static string HandleStopBroadcastRulePreview(string ruleId)
        {
            return DepartureControlSystem.Instance?.StopBroadcastRulePreviewJson(ruleId) ?? string.Empty;
        }

        private static string HandleSetBroadcastPreviewVolume(string volumeJson)
        {
            return DepartureControlSystem.Instance?.SetBroadcastPreviewVolumeJson(volumeJson) ?? string.Empty;
        }

        private static string HandleRefreshMetadata()
        {
            return DepartureControlSystem.Instance?.RefreshWorkbenchMetadataJson() ?? string.Empty;
        }

        private static string HandleSaveDraft(string requestJson)
        {
            string resultJson = BuildLegacyReadonlySaveResultJson();
            DispatchWorkbenchUISystem.PublishSaveResultJson(resultJson);
            return resultJson;
        }

        private static string HandleSaveNativeDraft(string requestJson)
        {
            string resultJson = DepartureControlSystem.Instance?.SaveWorkbenchDraftJson(requestJson) ?? string.Empty;
            DispatchWorkbenchUISystem.PublishSaveResultJson(resultJson);
            return resultJson;
        }

        private static string BuildLegacyReadonlySaveResultJson()
        {
            string snapshotJson = DepartureControlSystem.Instance?.RefreshWorkbenchSnapshotJson() ?? string.Empty;
            DepartureControlSystem.DispatchWorkbenchSnapshot snapshot =
                DispatchWorkbenchJson.Deserialize<DepartureControlSystem.DispatchWorkbenchSnapshot>(snapshotJson);
            DepartureControlSystem.DispatchWorkbenchSaveResult result = new DepartureControlSystem.DispatchWorkbenchSaveResult
            {
                success = false,
                errors = new[] { LegacyReadonlyMessage },
                warnings = Array.Empty<string>(),
                version = snapshot?.version ?? string.Empty,
                snapshot = snapshot
            };
            return DispatchWorkbenchJson.Serialize(result);
        }

        private static string HandleGetLocale()
        {
            return GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
        }

        private static bool RegisterAtEuisWhenReady()
        {
            if (s_EuisAppRegistered)
            {
                return true;
            }

            if (DateTime.UtcNow < s_NextEuisAssetLookupUtc)
            {
                return false;
            }

            // Do not move this call back into RegisterOnce().
            // ExtraUIScreens may not have exported its bridge yet when our host location becomes available,
            // so the EUIS app registration has to keep retrying from the updater until the bridge exists.
            ExecutableAsset euisAsset = FindExecutableAsset("ExtraUIScreens");
            if (euisAsset == null)
            {
                s_NextEuisAssetLookupUtc = DateTime.UtcNow.AddSeconds(2);
                if (!s_EuisDeferredLogged)
                {
                    Mod.log.Info("ExtraUIScreens not found yet; workbench EUIS registration deferred.");
                    s_EuisDeferredLogged = true;
                }
                return false;
            }

            s_NextEuisAssetLookupUtc = DateTime.MinValue;
            s_EuisDeferredLogged = false;

            Type bridgeType = euisAsset.assembly
                .GetExportedTypes()
                .FirstOrDefault(type => type.Name == "EuisExternalRegisterBridge");

            if (bridgeType == null)
            {
                Mod.log.Info("ExtraUIScreens found, but EuisExternalRegisterBridge was not exported.");
                return false;
            }

            bridgeType.GetMethod("RegisterModForEUIS")?.Invoke(null, new object[]
            {
                EuisModder,
                EuisAcronym,
                Delegate.Combine(new Action<Action<string, object[]>>(SetupCaller)),
                Delegate.Combine(new Action<Action<string, Delegate>>(SetupEventBinder)),
                Delegate.Combine(new Action<Action<string, Delegate>>(SetupCallBinder))
            });

            string baseUrl = ResolveAppBaseUrl();
            bridgeType.GetMethod("RegisterAppForEUIS")?.Invoke(null, new object[]
            {
                EuisModder,
                EuisAcronym,
                AppId,
                "RT Dispatch Workbench",
                baseUrl + "/dispatch-workbench-euis/dispatch-workbench-euis.js",
                baseUrl + "/dispatch-workbench-euis/dispatch-workbench-euis.css",
                "coui://uil/Standard/BusShelter.svg"
            });

            s_EuisAppRegistered = true;
            Mod.log.Info("DispatchWorkbench EUIS bridge registered from: " + baseUrl);
            return true;
        }

        private static string ResolveAppBaseUrl()
        {
            // Development override:
            // if a file named `dispatch-workbench-euis-dev-url.txt` exists in the live mod root and contains
            // a http(s) base URL, EUIS will load the workbench JS/CSS from there instead of the packed coui path.
            // This is only read during EUIS app registration at startup, so changing it still requires restarting
            // the game before the registration metadata picks up the new URL.
            try
            {
                string configPath = Path.Combine(s_ModRootPath, DevServerConfigFile);
                if (File.Exists(configPath))
                {
                    string configuredUrl = File.ReadAllText(configPath).Trim().TrimEnd('/');
                    if (configuredUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        configuredUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return configuredUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("DispatchWorkbench EUIS dev server override ignored: " + ex.Message);
            }

            return "coui://" + HostKey + "/UI";
        }

        private static void SetupCaller(Action<string, object[]> registerCaller)
        {
            s_EuisCaller = registerCaller;
        }

        private static void SetupEventBinder(Action<string, Delegate> registerEvent)
        {
            if (registerEvent == null)
            {
                return;
            }

            registerEvent("workbench.onSnapshotChanged", new Action<string>(_ => { }));
            registerEvent("workbench.onBroadcastSnapshotChanged", new Action<string>(_ => { }));
            registerEvent("workbench.onBroadcastAssetPreviewStateChanged", new Action<string>(_ => { }));
            registerEvent("workbench.onBroadcastRulePreviewStateChanged", new Action<string>(_ => { }));
        }

        private static void SetupCallBinder(Action<string, Delegate> registerCall)
        {
            if (registerCall == null)
            {
                return;
            }

            registerCall("workbench.loadSnapshot", new Func<string>(HandleLoadSnapshot));
            registerCall("workbench.refreshSnapshot", new Func<string>(HandleRefreshSnapshot));
            registerCall("workbench.loadBroadcastSnapshot", new Func<string, string>(HandleLoadBroadcastSnapshot));
            registerCall("workbench.refreshBroadcastSnapshot", new Func<string, string>(HandleRefreshBroadcastSnapshot));
            registerCall("workbench.loadBroadcastBindingSlotHints", new Func<string, string>(HandleLoadBroadcastBindingSlotHints));
            registerCall("workbench.loadBroadcastAssetBrowser", new Func<string, string>(HandleLoadBroadcastAssetBrowser));
            registerCall("workbench.importBroadcastExternalAssets", new Func<string, string>(HandleImportBroadcastExternalAssets));
            registerCall("workbench.deleteBroadcastAsset", new Func<string, string>(HandleDeleteBroadcastAsset));
            registerCall("workbench.deleteAllBroadcastAssets", new Func<string>(HandleDeleteAllBroadcastAssets));
            registerCall("workbench.saveBroadcastStationBinding", new Func<string, string>(HandleSaveBroadcastStationBinding));
            registerCall("workbench.saveBroadcastStationBindings", new Func<string, string>(HandleSaveBroadcastStationBindings));
            registerCall("workbench.autoBindBroadcastStationMappings", new Func<string, string>(HandleAutoBindBroadcastStationMappings));
            registerCall("workbench.saveBroadcastRules", new Func<string, string>(HandleSaveBroadcastRules));
            registerCall("workbench.saveBroadcastPlatformAnnouncement", new Func<string, string>(HandleSaveBroadcastPlatformAnnouncement));
            registerCall("workbench.copyBroadcastPlatformAnnouncementToAllStations", new Func<string, string>(HandleCopyBroadcastPlatformAnnouncementToAllStations));
            registerCall("workbench.applyBroadcastConfig", new Func<string, string>(HandleApplyBroadcastConfig));
            registerCall("workbench.openBroadcastAssetDirectoryPicker", new Func<string>(HandleOpenBroadcastAssetDirectoryPicker));
            registerCall("workbench.playBroadcastAssetPreview", new Func<string, string>(HandlePlayBroadcastAssetPreview));
            registerCall("workbench.stopBroadcastAssetPreview", new Func<string, string>(HandleStopBroadcastAssetPreview));
            registerCall("workbench.playBroadcastRulePreview", new Func<string, string>(HandlePlayBroadcastRulePreview));
            registerCall("workbench.stopBroadcastRulePreview", new Func<string, string>(HandleStopBroadcastRulePreview));
            registerCall("workbench.setBroadcastPreviewVolume", new Func<string, string>(HandleSetBroadcastPreviewVolume));
            registerCall("workbench.refreshMetadata", new Func<string>(HandleRefreshMetadata));
            registerCall("workbench.saveWorkbenchDraft", new Func<string, string>(HandleSaveDraft));
            registerCall("workbench.saveNativeWorkbenchDraft", new Func<string, string>(HandleSaveNativeDraft));
            registerCall("workbench.getLocale", new Func<string>(HandleGetLocale));
        }

        private static ExecutableAsset FindExecutableAsset(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName) || AssetDatabase.global == null)
            {
                return null;
            }

            return AssetDatabase.global.GetAsset<ExecutableAsset>(
                SearchFilter<ExecutableAsset>.ByCondition(
                    asset => asset.isLoaded && ((AssetData)asset).name.Equals(assetName, StringComparison.Ordinal),
                    false));
        }
    }
}
