namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal static class Api
    {
        internal static string Load(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string Refresh(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string Hints(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastBindingSlotHintsJson(lineId) ?? string.Empty;
        }

        internal static string Browse(string requestedPath)
        {
            return DispatchRuntimeSystem.Instance?.LoadBroadcastAssetBrowserJson(requestedPath) ?? string.Empty;
        }

        internal static string Import(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;
        }

        internal static string Delete(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.DeleteBroadcastAssetJson(assetName) ?? string.Empty;
        }

        internal static string DeleteAll()
        {
            return DispatchRuntimeSystem.Instance?.DeleteAllBroadcastAssetsJson() ?? string.Empty;
        }

        internal static string SaveMap(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;
        }

        internal static string SaveMaps(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;
        }

        internal static string AutoMap(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.AutoBindBroadcastStationMappingsJson(lineId) ?? string.Empty;
        }

        internal static string SaveRules(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastRulesJson(requestJson) ?? string.Empty;
        }

        internal static string SavePlatform(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.SaveBroadcastPlatformAnnouncementJson(requestJson) ?? string.Empty;
        }

        internal static string CopyPlatform(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson) ?? string.Empty;
        }

        internal static string Apply(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.ApplyBroadcastConfigJson(requestJson) ?? string.Empty;
        }

        internal static string Pick()
        {
            return DispatchRuntimeSystem.Instance?.OpenBroadcastAssetDirectoryPickerJson() ?? string.Empty;
        }

        internal static string Play(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.PlayBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string Stop(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.StopBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string PlayRule(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;
        }

        internal static string StopRule(string ruleId)
        {
            return DispatchRuntimeSystem.Instance?.StopBroadcastRulePreviewJson(ruleId) ?? string.Empty;
        }

        internal static string Volume(string volumeJson)
        {
            return DispatchRuntimeSystem.Instance?.SetBroadcastPreviewVolumeJson(volumeJson) ?? string.Empty;
        }
    }
}
