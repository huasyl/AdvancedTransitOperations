using System;
using System.Collections.Generic;
using Colossal.Core;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod
{
    internal static class WorkbenchApi
    {
        private const string HostKey = "rapidtransitmod";
        private const string Prefix = "suhua::rt.workbench.";
        private const string LegacyReadonlyMessage = "Legacy EUIS workbench is now read-only. Use the Dispatch Workbench schedule panel to edit and apply timetables.";

        private static bool s_IsInitialized;
        private static bool s_HostRegistered;
        private static bool s_BindingsRegistered;
        private static string s_ModRootPath = string.Empty;

        internal static void Initialize(string modRootPath)
        {
            if (!string.IsNullOrWhiteSpace(modRootPath) && string.IsNullOrWhiteSpace(s_ModRootPath))
            {
                s_ModRootPath = modRootPath;
            }

            Initialize();
        }

        internal static void Initialize()
        {
            if (s_IsInitialized)
            {
                return;
            }

            s_IsInitialized = true;
            MainThreadDispatcher.RegisterUpdater(RegisterUi);
        }

        internal static string LoadSnapshot()
        {
            string snapshotJson = DispatchRuntimeSystem.Instance?.LoadWorkbenchSnapshotJson() ?? string.Empty;
            WorkbenchEvents.PublishSnapshotJson(snapshotJson);
            return snapshotJson;
        }

        internal static string RefreshSnapshot()
        {
            string snapshotJson = DispatchRuntimeSystem.Instance?.RefreshWorkbenchSnapshotJson() ?? string.Empty;
            WorkbenchEvents.PublishSnapshotJson(snapshotJson);
            return snapshotJson;
        }

        internal static string LoadBroadcast(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string RefreshBroadcast(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string LoadSlotHints(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastBindingSlotHintsJson(lineId) ?? string.Empty;
        }

        internal static string LoadAssetBrowser(string requestedPath)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastAssetBrowserJson(requestedPath) ?? string.Empty;
        }

        internal static string ImportAssets(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;
        }

        internal static string DeleteAsset(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.DeleteBroadcastAssetJson(assetName) ?? string.Empty;
        }

        internal static string DeleteAllAssets()
        {
            return DispatchRuntimeSystem.Instance?.DeleteAllBroadcastAssetsJson() ?? string.Empty;
        }

        internal static string SaveStationBinding(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;
        }

        internal static string SaveStationBindings(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;
        }

        internal static string AutoBindStationMappings(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.AutoBindBroadcastStationMappingsJson(lineId) ?? string.Empty;
        }

        internal static string SaveRules(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastRulesJson(requestJson) ?? string.Empty;
        }

        internal static string SavePlatformAnnouncement(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastPlatformAnnouncementJson(requestJson) ?? string.Empty;
        }

        internal static string CopyPlatformAnnouncement(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson) ?? string.Empty;
        }

        internal static string ApplyBroadcast(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.ApplyBroadcastConfigJson(requestJson) ?? string.Empty;
        }

        internal static string PickAssetDirectory()
        {
            return DispatchRuntimeSystem.Instance?.OpenBroadcastAssetDirectoryPickerJson() ?? string.Empty;
        }

        internal static string PlayAssetPreview(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.PlayBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string StopAssetPreview(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.StopBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string PlayRulePreview(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;
        }

        internal static string StopRulePreview(string ruleId)
        {
            return DispatchRuntimeSystem.Instance?.StopBroadcastRulePreviewJson(ruleId) ?? string.Empty;
        }

        internal static string SetPreviewVolume(string volumeJson)
        {
            return DispatchRuntimeSystem.Instance?.SetBroadcastPreviewVolumeJson(volumeJson) ?? string.Empty;
        }

        internal static string RefreshMetadata()
        {
            return DispatchRuntimeSystem.Instance?.RefreshWorkbenchMetadataJson() ?? string.Empty;
        }

        internal static string LoadPlanner()
        {
            return DispatchRuntimeSystem.Instance?.LoadPlannerContextJson() ?? string.Empty;
        }

        internal static string StartPlannerJob(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.StartWorkbenchPlannerJobJson(requestJson) ?? string.Empty;
        }

        internal static string GetPlannerJobStatus(string jobId)
        {
            return DispatchRuntimeSystem.Instance?.GetWorkbenchPlannerJobStatusJson(jobId) ?? string.Empty;
        }

        internal static string RunPlanner(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.RunWorkbenchPlannerJson(requestJson) ?? string.Empty;
        }

        internal static string GetObservationSnapshot()
        {
            return DispatchRuntimeSystem.Instance?.GetRuntimeObservationSnapshotJson() ?? string.Empty;
        }

        internal static string SaveDraft(string requestJson)
        {
            string resultJson = BuildLegacyReadonlySaveResultJson();
            WorkbenchEvents.PublishSaveResult(resultJson);
            return resultJson;
        }

        internal static string SaveNativeDraft(string requestJson)
        {
            string resultJson = DispatchRuntimeSystem.Instance?.SaveWorkbenchDraftJson(requestJson) ?? string.Empty;
            WorkbenchEvents.PublishSaveResult(resultJson);
            return resultJson;
        }

        internal static string StartSaveOperation(string requestJson)
        {
            Mod.log.Info($"[WorkbenchSaveOperationBridge] startNativeSaveOperation length={requestJson?.Length ?? 0}");
            return DispatchRuntimeSystem.Instance?.StartWorkbenchSaveOperationJson(requestJson) ?? string.Empty;
        }

        internal static string GetSaveOperationStatus(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                Mod.log.Info("[WorkbenchSaveOperationBridge] getNativeSaveOperationStatus empty id");
            }

            return DispatchRuntimeSystem.Instance?.GetWorkbenchSaveOperationStatusJson(operationId) ?? string.Empty;
        }

        internal static string GetLocale()
        {
            return GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
        }

        private static bool RegisterUi()
        {
            RegisterHost();
            RegisterBindings();
            return s_HostRegistered && s_BindingsRegistered;
        }

        private static void RegisterHost()
        {
            if (s_HostRegistered || string.IsNullOrWhiteSpace(s_ModRootPath))
            {
                return;
            }

            var uiSystem = GameManager.instance?.userInterface?.view?.uiSystem;
            if (uiSystem == null)
            {
                return;
            }

            uiSystem.AddHostLocation(
                HostKey,
                new HashSet<(string, int)> { (s_ModRootPath, 0) },
                true);
            s_HostRegistered = true;
            Mod.log.Info("DispatchWorkbench host location registered.");
        }

        private static void RegisterBindings()
        {
            if (s_BindingsRegistered)
            {
                return;
            }

            var view = GameManager.instance?.userInterface?.view?.View;
            if (view == null)
            {
                return;
            }

            view.BindCall(Prefix + "loadSnapshot", new Func<string>(LoadSnapshot));
            view.BindCall(Prefix + "refreshSnapshot", new Func<string>(RefreshSnapshot));
            view.BindCall(Prefix + "loadBroadcastSnapshot", new Func<string, string>(LoadBroadcast));
            view.BindCall(Prefix + "refreshBroadcastSnapshot", new Func<string, string>(RefreshBroadcast));
            view.BindCall(Prefix + "loadBroadcastBindingSlotHints", new Func<string, string>(LoadSlotHints));
            view.BindCall(Prefix + "loadBroadcastAssetBrowser", new Func<string, string>(LoadAssetBrowser));
            view.BindCall(Prefix + "importBroadcastExternalAssets", new Func<string, string>(ImportAssets));
            view.BindCall(Prefix + "deleteBroadcastAsset", new Func<string, string>(DeleteAsset));
            view.BindCall(Prefix + "deleteAllBroadcastAssets", new Func<string>(DeleteAllAssets));
            view.BindCall(Prefix + "saveBroadcastStationBinding", new Func<string, string>(SaveStationBinding));
            view.BindCall(Prefix + "saveBroadcastStationBindings", new Func<string, string>(SaveStationBindings));
            view.BindCall(Prefix + "autoBindBroadcastStationMappings", new Func<string, string>(AutoBindStationMappings));
            view.BindCall(Prefix + "saveBroadcastRules", new Func<string, string>(SaveRules));
            view.BindCall(Prefix + "saveBroadcastPlatformAnnouncement", new Func<string, string>(SavePlatformAnnouncement));
            view.BindCall(Prefix + "copyBroadcastPlatformAnnouncementToAllStations", new Func<string, string>(CopyPlatformAnnouncement));
            view.BindCall(Prefix + "applyBroadcastConfig", new Func<string, string>(ApplyBroadcast));
            view.BindCall(Prefix + "openBroadcastAssetDirectoryPicker", new Func<string>(PickAssetDirectory));
            view.BindCall(Prefix + "playBroadcastAssetPreview", new Func<string, string>(PlayAssetPreview));
            view.BindCall(Prefix + "stopBroadcastAssetPreview", new Func<string, string>(StopAssetPreview));
            view.BindCall(Prefix + "playBroadcastRulePreview", new Func<string, string>(PlayRulePreview));
            view.BindCall(Prefix + "stopBroadcastRulePreview", new Func<string, string>(StopRulePreview));
            view.BindCall(Prefix + "setBroadcastPreviewVolume", new Func<string, string>(SetPreviewVolume));
            view.BindCall(Prefix + "refreshMetadata", new Func<string>(RefreshMetadata));
            view.BindCall(Prefix + "loadPlannerContext", new Func<string>(LoadPlanner));
            view.BindCall(Prefix + "exportPlannerInput", new Func<string>(LoadPlanner));
            view.BindCall(Prefix + "startPlannerJob", new Func<string, string>(StartPlannerJob));
            view.BindCall(Prefix + "getPlannerJobStatus", new Func<string, string>(GetPlannerJobStatus));
            view.BindCall(Prefix + "runPlanner", new Func<string, string>(RunPlanner));
            view.BindCall(Prefix + "getRuntimeObservationSnapshot", new Func<string>(GetObservationSnapshot));
            view.BindCall(Prefix + "saveWorkbenchDraft", new Func<string, string>(SaveDraft));
            view.BindCall(Prefix + "saveNativeWorkbenchDraft", new Func<string, string>(SaveNativeDraft));
            view.BindCall(Prefix + "startNativeSaveOperation", new Func<string, string>(StartSaveOperation));
            view.BindCall(Prefix + "getNativeSaveOperationStatus", new Func<string, string>(GetSaveOperationStatus));
            view.BindCall(Prefix + "getLocale", new Func<string>(GetLocale));

            s_BindingsRegistered = true;
            Mod.log.Info("DispatchWorkbench API bindings registered.");
        }

        private static string BuildLegacyReadonlySaveResultJson()
        {
            string snapshotJson = DispatchRuntimeSystem.Instance?.RefreshWorkbenchSnapshotJson() ?? string.Empty;
            DispatchWorkbenchSnapshot snapshot =
                DispatchWorkbenchJson.Deserialize<DispatchWorkbenchSnapshot>(snapshotJson);
            DispatchWorkbenchSaveResult result = new DispatchWorkbenchSaveResult
            {
                success = false,
                errors = new[] { LegacyReadonlyMessage },
                warnings = Array.Empty<string>(),
                version = snapshot?.version ?? string.Empty,
                snapshot = snapshot
            };
            return DispatchWorkbenchJson.Serialize(result);
        }
    }
}
