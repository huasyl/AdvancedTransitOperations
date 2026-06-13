using System.Runtime.Serialization;

namespace RapidTransitMod
{
    [DataContract]
    public class DispatchWorkbenchSnapshot
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string selectedLineId;
        [DataMember]
        public string selectedEditLine;
        [DataMember]
        public DispatchWorkbenchMergedView mergedView;
        [DataMember]
        public DispatchWorkbenchLineDto[] lines;
        [DataMember]
        public DispatchWorkbenchDepotDto[] depots;
        [DataMember]
        public DispatchWorkbenchStationDto[] stations;
        [DataMember]
        public DispatchWorkbenchTripDto[] trips;
        [DataMember]
        public DispatchWorkbenchManualRowDto[] manualRows;
        [DataMember]
        public DispatchWorkbenchAutoRuleDto[] autoRules;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
        [DataMember]
        public DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] combinedDraftRows;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] appliedRows;
        [DataMember]
        public DispatchWorkbenchPlanRefDto[] planRefs;
        [DataMember]
        public string version;
        [DataMember]
        public string sourceMode;
        [DataMember]
        public bool rulesApplied;
        [DataMember]
        public bool draftApplied;
        [DataMember]
        public RuntimeFeatureSettingsDto featureSettings;
    }

    [DataContract]
    public class DispatchWorkbenchCatalogEvent
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string version;
    }

    [DataContract]
    public class DispatchWorkbenchHostStateDto
    {
        [DataMember]
        public string phase;
        [DataMember]
        public string mode;
        [DataMember]
        public string activePage;
        [DataMember]
        public string selectedLineId;
        [DataMember]
        public string selectedEditLine;
    }

    [DataContract]
    public class RuntimeFeatureSettingsDto
    {
        [DataMember]
        public bool dispatchEnabled;
        [DataMember]
        public bool bypassEnabled;
        [DataMember]
        public bool broadcastEnabled;
        [DataMember]
        public bool depotLockEnabled;
    }

    [DataContract]
    public class DispatchWorkbenchLineSettingDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public int originHoldLimitMinutes;
        [DataMember]
        public int maxStationDwellMinutes;
        [DataMember]
        public string allowedDepotId;
        [DataMember]
        public string serviceKind;
    }

    [DataContract]
    public class DispatchWorkbenchDepotDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string name;
        [DataMember]
        public string transportType;
    }

    [DataContract]
    public class DispatchWorkbenchSaveRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string selectedLineId;
        [DataMember]
        public string selectedEditLine;
        [DataMember]
        public DispatchWorkbenchMergedView mergedView;
        [DataMember]
        public DispatchWorkbenchManualRowDto[] manualRows;
        [DataMember]
        public DispatchWorkbenchAutoRuleDto[] autoRules;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
        [DataMember]
        public DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId;
        [DataMember]
        public DispatchWorkbenchLineSettingDto[] lineSettings;
        [DataMember]
        public bool markRulesApplied;
        [DataMember]
        public bool applyDraft;
        [DataMember]
        public bool nativeScheduleWriter;
        [DataMember]
        public bool? returnSnapshot;
        [DataMember]
        public DispatchWorkbenchPlanRefDto[] planRefs;
        [DataMember]
        public DispatchWorkbenchPlannerImportContractDto plannerImportContract;
    }

    [DataContract]
    public class DispatchWorkbenchLineDraftRowsDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
    }

    [DataContract]
    public class DispatchWorkbenchPlanRefDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public DispatchWorkbenchPlannerImportContractDto contract;
    }

    [DataContract]
    public class DispatchWorkbenchSaveResult
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string[] errors;
        [DataMember]
        public string[] warnings;
        [DataMember]
        public string version;
        [DataMember]
        public string[] appliedLineIds;
        [DataMember]
        public DispatchWorkbenchSnapshot snapshot;
    }

    [DataContract]
    public class DispatchWorkbenchSaveOperationStatusDto
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
        public DispatchWorkbenchSaveResult result;
    }

    [DataContract]
    public class DispatchWorkbenchPersistentState
    {
        [DataMember]
        public string preferredLineId;
        [DataMember]
        public DispatchWorkbenchModePreferredLineDto[] preferredLineIdsByMode;
        [DataMember]
        public DispatchWorkbenchLineSettingDto[] lineSettings;
        [DataMember]
        public DispatchWorkbenchPersistedDraftState[] drafts;
        [DataMember]
        public string broadcastAssetDirectory;
        [DataMember]
        public BroadcastWorkbenchPersistedAssetState[] broadcastAssets;
        [DataMember]
        public BroadcastWorkbenchPersistedAssetCatalogState[] broadcastAssetStates;
        [DataMember]
        public BroadcastWorkbenchPersistedLineBindingState[] broadcastDraftLineBindings;
        [DataMember]
        public BroadcastWorkbenchPersistedRuleState[] broadcastDraftRules;
        [DataMember]
        public BroadcastWorkbenchPersistedPlatformAnnouncementState[] broadcastDraftPlatformAnnouncements;
        [DataMember]
        public BroadcastWorkbenchPersistedLineBindingState[] broadcastLineBindings;
        [DataMember]
        public BroadcastWorkbenchPersistedRuleState[] broadcastRules;
        [DataMember]
        public BroadcastWorkbenchPersistedPlatformAnnouncementState[] broadcastPlatformAnnouncements;
        [DataMember]
        public BroadcastWorkbenchPersistedAppliedState broadcastAppliedState;
        [DataMember]
        public int? broadcastDraftVolume;
        [DataMember]
        public BroadcastWorkbenchPersistedVolumeState[] broadcastVolumeStates;
        [DataMember(EmitDefaultValue = false)]
        public RuntimeFeatureSettingsDto featureSettings;
    }

    [DataContract]
    public class DispatchWorkbenchModePreferredLineDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string lineId;
    }

    [DataContract]
    public class DispatchWorkbenchPlannerImportContractDto
    {
        [DataMember]
        public string draftKey;
        [DataMember]
        public string importedFrom;
        [DataMember]
        public string importedPlanId;
        [DataMember]
        public string importedObjectiveId;
        [DataMember]
        public string[] importedLineIds;
        [DataMember]
        public DispatchPlannerRequestEchoDto requestEcho;
        [DataMember]
        public DispatchPlannerPlanDetailDto plan;
    }

    [DataContract]
    public class DispatchWorkbenchPersistedDraftState
    {
        [DataMember]
        public string lineKey;
        [DataMember]
        public string selectedLineId;
        [DataMember]
        public string selectedEditLine;
        [DataMember]
        public DispatchWorkbenchMergedView mergedView;
        [DataMember]
        public DispatchWorkbenchManualRowDto[] manualRows;
        [DataMember]
        public DispatchWorkbenchAutoRuleDto[] autoRules;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
        [DataMember]
        public bool rulesApplied;
        [DataMember]
        public bool draftApplied;
        [DataMember]
        public DispatchWorkbenchPlannerImportContractDto plannerImportContract;
    }

    [DataContract]
    public class DispatchWorkbenchMergedView
    {
        [DataMember]
        public string localLineId;
        [DataMember]
        public string expressLineId;
        [DataMember]
        public string[] localLineIds;
        [DataMember]
        public string[] expressLineIds;
        [DataMember]
        public bool isLoop;
        [DataMember]
        public string turnbackStationId;
        [DataMember]
        public string direction;
        [DataMember]
        public string windowStart;
        [DataMember]
        public string windowEnd;
    }

    [DataContract]
    public class DispatchWorkbenchLineDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string sourceLineId;
        [DataMember]
        public string name;
        [DataMember]
        public string kind;
        [DataMember]
        public string direction;
        [DataMember]
        public int stationCount;
        [DataMember]
        public string color;
        [DataMember]
        public string originStationId;
        [DataMember]
        public string originStationName;
        [DataMember]
        public int originHoldLimitMinutes;
        [DataMember]
        public int maxStationDwellMinutes;
        [DataMember]
        public string transportType;
        [DataMember]
        public string allowedDepotId;
        [DataMember]
        public bool dispatchSupported;
        [DataMember]
        public string unsupportedReason;
        [DataMember]
        public string originStatus;
        [DataMember]
        public string originMessageKey;
    }

    [DataContract]
    public class DispatchWorkbenchStationConflictDto
    {
        [DataMember]
        public string assetName;
        [DataMember]
        public string suggestedLang;
    }

    [DataContract]
    public class DispatchWorkbenchStationDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string name;
        [DataMember]
        public int order;
        [DataMember]
        public float distance;
        [DataMember]
        public bool hasSiding;
        [DataMember]
        public DispatchWorkbenchStationConflictDto[] conflictAssets;
    }

    [DataContract]
    public class DispatchWorkbenchTripStopDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public string time;
        [DataMember]
        public string arrivalTime;
        [DataMember]
        public string departureTime;
        [DataMember]
        public string stopType;
        [DataMember]
        public int? waitMinutes;
    }

    [DataContract]
    public class DispatchWorkbenchTripDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public string kind;
        [DataMember]
        public string depart;
        [DataMember]
        public int realtimeSegment;
        [DataMember]
        public float realtimeProgress;
        [DataMember]
        public string realtimeFromStationId;
        [DataMember]
        public string realtimeToStationId;
        [DataMember]
        public string realtimeTime;
        [DataMember]
        public DispatchWorkbenchTripStopDto[] stops;
    }

    [DataContract]
    public class DispatchWorkbenchManualRowDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public string time;
        [DataMember]
        public string kind;
        [DataMember]
        public string offsetMode;
        [DataMember]
        public string offsetMinutes;
    }

    [DataContract]
    public class DispatchWorkbenchAutoRuleDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public bool enabled;
        [DataMember]
        public string start;
        [DataMember]
        public string end;
        [DataMember]
        public string kind;
        [DataMember]
        public double departuresPerHour;
        [DataMember]
        public double localPerHour;
        [DataMember]
        public double expressPerHour;
        [DataMember]
        public string expressOffsetMode;
        [DataMember]
        public int expressOffsetMinutes;
    }

    [DataContract]
    public class DispatchWorkbenchStagedRowDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public string time;
        [DataMember]
        public string kind;
        [DataMember]
        public string source;
        [DataMember]
        public string note;
    }
}
