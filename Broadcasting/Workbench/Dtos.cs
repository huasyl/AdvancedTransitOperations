using System.Runtime.Serialization;

namespace RapidTransitMod
{
    [DataContract]
    public class BroadcastWorkbenchAssetDto
    {
        [DataMember]
        public string name;
        [DataMember]
        public string desc;
        [DataMember]
        public string length;
        [DataMember]
        public string path;
        [DataMember]
        public string extension;
        [DataMember]
        public bool missing;
    }

    [DataContract]
    public class BroadcastWorkbenchSnapshot
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string selectedLineId;
        [DataMember]
        public DispatchWorkbenchLineDto[] lines;
        [DataMember]
        public DispatchWorkbenchStationDto[] stations;
        [DataMember]
        public BroadcastWorkbenchTurnbackPointDto[] turnbackPoints;
        [DataMember]
        public BroadcastWorkbenchStationBindingDto[] stationBindings;
        [DataMember]
        public BroadcastWorkbenchRuleDto[] rules;
        [DataMember]
        public BroadcastWorkbenchPlatformAnnouncementDto[] platformAnnouncements;
        [DataMember]
        public string assetDirectory;
        [DataMember]
        public BroadcastWorkbenchAssetDto[] assets;
        [DataMember]
        public string version;
        [DataMember]
        public string sourceMode;
        [DataMember]
        public bool lineApplied;
        [DataMember]
        public bool lineDraftDirty;
        [DataMember]
        public bool volumeDirty;
        [DataMember]
        public bool draftApplied;
        [DataMember]
        public bool draftDirty;
        [DataMember]
        public int volume;
        [DataMember]
        public string[] warnings;
    }

    [DataContract]
    public class BroadcastWorkbenchTurnbackPointDto
    {
        [DataMember]
        public int index;
        [DataMember]
        public string stationId;
        [DataMember]
        public string stationName;
        [DataMember]
        public bool resolved;
    }

    [DataContract]
    public class BroadcastWorkbenchDirectoryPickerResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public bool pending;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchExternalAssetFileDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string name;
        [DataMember]
        public string fullPath;
    }

    [DataContract]
    public class BroadcastWorkbenchStationBindingDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public string lang;
        [DataMember]
        public int langIndex;
        [DataMember]
        public string assetName;
    }

    [DataContract]
    public class BroadcastWorkbenchRuleNodeDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string type;
        [DataMember]
        public string name;
        [DataMember]
        public string nameKey;
        [DataMember]
        public string desc;
        [DataMember]
        public string descKey;
        [DataMember]
        public int langIndex;
        [DataMember]
        public float delaySeconds;
    }

    [DataContract]
    public class BroadcastWorkbenchRuleDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string title;
        [DataMember]
        public string titleKey;
        [DataMember]
        public string triggerId;
        [DataMember]
        public string trigger;
        [DataMember]
        public string triggerKey;
        [DataMember]
        public BroadcastWorkbenchRuleNodeDto[] nodes;
    }

    [DataContract]
    public class BroadcastWorkbenchPlatformAnnouncementDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string stationId;
        [DataMember]
        public string stationName;
        [DataMember]
        public string title;
        [DataMember]
        public string uiTriggerId;
        [DataMember]
        public bool enabled;
        [DataMember]
        public string triggerId;
        [DataMember]
        public int cooldownGameMinutes;
        [DataMember]
        public BroadcastWorkbenchRuleNodeDto[] nodes;
    }

    [DataContract]
    public class BroadcastWorkbenchExternalAssetBrowserSnapshot
    {
        [DataMember]
        public string rootPath;
        [DataMember]
        public string currentPath;
        [DataMember]
        public string parentPath;
        [DataMember]
        public string[] folders;
        [DataMember]
        public BroadcastWorkbenchExternalAssetFileDto[] files;
        [DataMember]
        public string[] allowedExtensions;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchImportExternalAssetsRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string currentPath;
        [DataMember]
        public string[] selectedPaths;
    }

    [DataContract]
    public class BroadcastWorkbenchImportExternalAssetsResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public int importedCount;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchAssetPreviewResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
        [DataMember]
        public string assetName;
    }

    [DataContract]
    public class BroadcastWorkbenchAssetPreviewStateDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string assetName;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchRulePreviewRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string lineId;
        [DataMember]
        public string ruleId;
        [DataMember]
        public BroadcastWorkbenchRuleDto rule;
        [DataMember]
        public int volume;
    }

    [DataContract]
    public class BroadcastWorkbenchRulePreviewResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
        [DataMember]
        public string ruleId;
    }

    [DataContract]
    public class BroadcastWorkbenchRulePreviewStateDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string ruleId;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchVolumeResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public int volume;
        [DataMember]
        public bool volumeDirty;
        [DataMember]
        public BroadcastWorkbenchSnapshot snapshot;
    }

    [DataContract]
    public class BroadcastWorkbenchBindingSlotHintDto
    {
        [DataMember]
        public int langIndex;
        [DataMember]
        public string[] labels;
    }

    [DataContract]
    public class BroadcastWorkbenchBindingSlotHintsResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public BroadcastWorkbenchBindingSlotHintDto[] slotHints;
    }

    [DataContract]
    public class BroadcastWorkbenchDeleteAssetResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchDeleteAllAssetsResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchAutoBindStationMappingsResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public int boundCount;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchSaveStationBindingRequest
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string stationId;
        [DataMember]
        public string assetName;
    }

    [DataContract]
    public class BroadcastWorkbenchSaveStationBindingsRequest
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string stationId;
        [DataMember]
        public BroadcastWorkbenchStationBindingDto[] bindings;
    }

    [DataContract]
    public class BroadcastWorkbenchSaveStationBindingResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchSaveRulesRequest
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public BroadcastWorkbenchRuleDto[] rules;
    }

    [DataContract]
    public class BroadcastWorkbenchSaveRulesResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
    }

    [DataContract]
    public class BroadcastWorkbenchSavePlatformAnnouncementRequest
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string stationId;
        [DataMember]
        public string stationName;
        [DataMember]
        public string title;
        [DataMember]
        public string uiTriggerId;
        [DataMember]
        public bool enabled;
        [DataMember]
        public BroadcastWorkbenchRuleNodeDto[] nodes;
    }

    [DataContract]
    public class BroadcastWorkbenchSavePlatformAnnouncementResult
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public BroadcastWorkbenchSnapshot snapshot;
    }

    [DataContract]
    public class ApplyRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public ApplyLineConfig[] lines;
        [DataMember]
        public int? volume;
        [DataMember]
        public bool volumeDirty;
    }

    [DataContract]
    public class ApplyLineConfig
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public BroadcastWorkbenchStationBindingDto[] stationBindings;
        [DataMember]
        public BroadcastWorkbenchRuleDto[] rules;
        [DataMember]
        public BroadcastWorkbenchPlatformAnnouncementDto[] platformAnnouncements;
    }

    [DataContract]
    public class ApplyResult
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public string version;
        [DataMember]
        public string[] appliedLineIds;
        [DataMember]
        public bool volumeApplied;
        [DataMember]
        public string[] warnings;
    }

    [DataContract]
    public class ApplyOperationStatusDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string operationId;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
        [DataMember]
        public ApplyResult result;
    }

    [DataContract]
    public class BroadcastWorkbenchApplyRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string lineId;
    }

    [DataContract]
    public class BroadcastWorkbenchApplyResult
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public BroadcastWorkbenchSnapshot snapshot;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedAssetState
    {
        [DataMember]
        public string name;
        [DataMember]
        public string desc;
        [DataMember]
        public string length;
        [DataMember]
        public string extension;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedAssetCatalogState
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string assetDirectory;
        [DataMember]
        public BroadcastWorkbenchPersistedAssetState[] assets;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedLineBindingState
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public BroadcastWorkbenchStationBindingDto[] stationBindings;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedRuleState
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public BroadcastWorkbenchRuleDto[] rules;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedPlatformAnnouncementState
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public BroadcastWorkbenchPlatformAnnouncementDto[] announcements;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedAppliedState
    {
        [DataMember]
        public string[] lineIds;
        [DataMember]
        public int? volume;
    }

    [DataContract]
    public class BroadcastWorkbenchPersistedVolumeState
    {
        [DataMember]
        public string mode;
        [DataMember]
        public int? draftVolume;
        [DataMember]
        public int? appliedVolume;
    }
}
