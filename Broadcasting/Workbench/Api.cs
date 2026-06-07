namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal static class Api
    {
        internal static string Load(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastWorkbenchSnapshotJson(requestJson) ?? string.Empty;
        }

        internal static string Refresh(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.RefreshBroadcastWorkbenchSnapshotJson(requestJson) ?? string.Empty;
        }

        internal static string Hints(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastBindingSlotHintsJson(requestJson) ?? string.Empty;
        }

        internal static string Browse(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.LoadBroadcastAssetBrowserJson(requestJson) ?? string.Empty;
        }

        internal static string Import(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;
        }

        internal static string Delete(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.DeleteBroadcastAssetJson(requestJson) ?? string.Empty;
        }

        internal static string DeleteAll(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.DeleteAllBroadcastAssetsJson(requestJson) ?? string.Empty;
        }

        internal static string SaveMap(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;
        }

        internal static string SaveMaps(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;
        }

        internal static string AutoMap(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.AutoBindBroadcastStationMappingsJson(requestJson) ?? string.Empty;
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

        internal static string Pick(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.OpenBroadcastAssetDirectoryPickerJson(requestJson) ?? string.Empty;
        }

        internal static string Play(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.PlayBroadcastAssetPreviewJson(requestJson) ?? string.Empty;
        }

        internal static string Stop(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.StopBroadcastAssetPreviewJson(requestJson) ?? string.Empty;
        }

        internal static string PlayRule(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;
        }

        internal static string StopRule(string requestJson)
        {
            return DispatchRuntimeSystem.Instance?.m_AnnouncementWorkbench?.StopBroadcastRulePreviewJson(requestJson) ?? string.Empty;
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
