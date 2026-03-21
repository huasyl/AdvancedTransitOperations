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
        private const string DevServerConfigFile = "dispatch-workbench-euis-dev-url.txt";

        private static bool s_IsInitialized;
        private static bool s_IsRegistered;
        private static bool s_HostRegistered;
        private static bool s_EuisAppRegistered;
        private static string s_ModRootPath = string.Empty;
        private static Action<string, object[]> s_EuisCaller;

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
            view.BindCall(EuisModder + "::" + EuisAcronym + ".workbench.saveWorkbenchDraft", new Func<string, string>(HandleSaveDraft));
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

        private static string HandleSaveDraft(string requestJson)
        {
            string resultJson = DepartureControlSystem.Instance?.SaveWorkbenchDraftJson(requestJson) ?? string.Empty;
            DispatchWorkbenchUISystem.PublishSaveResultJson(resultJson);
            return resultJson;
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

            // Do not move this call back into RegisterOnce().
            // ExtraUIScreens may not have exported its bridge yet when our host location becomes available,
            // so the EUIS app registration has to keep retrying from the updater until the bridge exists.
            ExecutableAsset euisAsset = FindExecutableAsset("ExtraUIScreens");
            if (euisAsset == null)
            {
                Mod.log.Info("ExtraUIScreens not found yet; workbench EUIS registration deferred.");
                return false;
            }

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
        }

        private static void SetupCallBinder(Action<string, Delegate> registerCall)
        {
            if (registerCall == null)
            {
                return;
            }

            registerCall("workbench.loadSnapshot", new Func<string>(HandleLoadSnapshot));
            registerCall("workbench.refreshSnapshot", new Func<string>(HandleRefreshSnapshot));
            registerCall("workbench.saveWorkbenchDraft", new Func<string, string>(HandleSaveDraft));
            registerCall("workbench.getLocale", new Func<string>(HandleGetLocale));
        }

        private static ExecutableAsset FindExecutableAsset(string assetName)
        {
            return AssetDatabase.global.GetAsset<ExecutableAsset>(
                SearchFilter<ExecutableAsset>.ByCondition(
                    asset => asset.isLoaded && ((AssetData)asset).name.Equals(assetName, StringComparison.Ordinal),
                    false));
        }
    }
}
