namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal static class Api
    {
        internal static string Load(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string Refresh(string preferredLineId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;
        }

        internal static string Hints(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastBindingSlotHintsJson(lineId) ?? string.Empty;
        }

        internal static string Browse(string requestedPath)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastAssetBrowserJson(requestedPath) ?? string.Empty;
        }

        internal static string Import(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;
        }

        internal static string Delete(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.DeleteBroadcastAssetJson(assetName) ?? string.Empty;
        }

        internal static string DeleteAll()
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.DeleteAllBroadcastAssetsJson() ?? string.Empty;
        }

        internal static string SaveMap(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;
        }

        internal static string SaveMaps(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;
        }

        internal static string AutoMap(string lineId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.AutoBindBroadcastStationMappingsJson(lineId) ?? string.Empty;
        }

        internal static string SaveRules(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastRulesJson(requestJson) ?? string.Empty;
        }

        internal static string SavePlatform(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastPlatformAnnouncementJson(requestJson) ?? string.Empty;
        }

        internal static string CopyPlatform(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson) ?? string.Empty;
        }

        internal static string Apply(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.ApplyBroadcastConfigJson(requestJson) ?? string.Empty;
        }

        internal static string Pick()
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.OpenBroadcastAssetDirectoryPickerJson() ?? string.Empty;
        }

        internal static string Play(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.PlayBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string Stop(string assetName)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.StopBroadcastAssetPreviewJson(assetName) ?? string.Empty;
        }

        internal static string PlayRule(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;
        }

        internal static string StopRule(string ruleId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.StopBroadcastRulePreviewJson(ruleId) ?? string.Empty;
        }

        internal static string Volume(string volumeJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SetBroadcastPreviewVolumeJson(volumeJson) ?? string.Empty;
        }

        internal static string StartApply(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.StartBroadcastApplyOperationJson(requestJson) ?? string.Empty;
        }

        internal static string ApplyStatus(string operationId)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.GetBroadcastApplyOperationStatusJson(operationId) ?? string.Empty;
        }
    }
}
