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
        public DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] appliedRows;
        [DataMember]
        public string version;
        [DataMember]
        public string sourceMode;
        [DataMember]
        public bool draftApplied;
        [DataMember]
        public RuntimeFeatureSettingsDto featureSettings;
        [DataMember]
        public DispatchWorkbenchCleanupInfoDto cleanupInfo;
        [DataMember]
        public int clientRequestSequence;
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
    public class DispatchWorkbenchLineInvalidationEvent
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string version;
        [DataMember]
        public string[] lineIds;
        [DataMember]
        public DispatchWorkbenchCleanupReasonDto[] reasons;
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
        public bool clearPartialTimetable;
        [DataMember]
        public bool nativeScheduleWriter;
        [DataMember]
        public bool? returnSnapshot;
        [DataMember]
        public DispatchWorkbenchPlanRefDto[] planRefs;
        [DataMember]
        public DispatchWorkbenchPlannerImportContractDto plannerImportContract;
        [DataMember]
        public string[] removedLineIds;
        [DataMember]
        public DispatchWorkbenchLineRuntimeRefDto[] lineRuntimeRefs;
        [DataMember]
        public int clientRequestSequence;
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
    public class DispatchWorkbenchLineRuntimeRefDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string sourceLineId;
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
        [DataMember]
        public DispatchWorkbenchCleanupInfoDto cleanupInfo;
    }

    [DataContract]
    public class DispatchWorkbenchCleanupInfoDto
    {
        [DataMember]
        public string[] removedAppliedLineIds;
        [DataMember]
        public string[] removedDraftLineIds;
        [DataMember]
        public string[] removedLineSettingIds;
        [DataMember]
        public DispatchWorkbenchCleanupReasonDto[] reasons;
    }

    [DataContract]
    public class DispatchWorkbenchCleanupReasonDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string reason;
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
        [DataMember(EmitDefaultValue = false)]
        public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
        [DataMember(EmitDefaultValue = false)]
        public string[] selectedBypassStationIds;
        [DataMember(EmitDefaultValue = false)]
        public DispatchPlannerChangedRowDto[] changedRows;
        [DataMember(EmitDefaultValue = false)]
        public DispatchPlannerScheduleActionDto[] structuredActions;
        [DataMember(EmitDefaultValue = false)]
        public DispatchPlannerRiskItemDto[] riskItems;
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
        [DataMember(EmitDefaultValue = false)]
        public DispatchWorkbenchManualRowDto[] manualRows;
        [DataMember(EmitDefaultValue = false)]
        public DispatchWorkbenchAutoRuleDto[] autoRules;
        [DataMember(EmitDefaultValue = false)]
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] stagedRows;
        [DataMember(EmitDefaultValue = false)]
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
    public class DispatchWorkbenchTimedStopDto
    {
        [DataMember]
        public string stopKey;
        [DataMember]
        public int? arrive;
        [DataMember]
        public int? depart;
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
        [DataMember(EmitDefaultValue = false)]
        public string stopSig;
        [DataMember(EmitDefaultValue = false)]
        public DispatchWorkbenchTimedStopDto[] timedStops;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartSegmentDto
    {
        [DataMember]
        public string fromStopKey;
        [DataMember]
        public string toStopKey;
        [DataMember]
        public int fromWaypointIndex;
        [DataMember]
        public int toWaypointIndex;
        [DataMember]
        public uint segmentFrames;
        [DataMember]
        public int segmentMinutes;
        [DataMember]
        public double segmentMinutesExact;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartDwellDto
    {
        [DataMember]
        public string stopKey;
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public float averageFrames;
        [DataMember]
        public int averageMinutes;
        [DataMember]
        public int sampleCount;
        [DataMember]
        public bool hasObservation;
    }

    [DataContract]
    public class DispatchWorkbenchRunTimeQueryRequestDto
    {
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string source;
    }

    [DataContract]
    public class DispatchWorkbenchRunTimeQueryStatusDto
    {
        [DataMember]
        public string queryId;
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string state;
        [DataMember]
        public string resultId;
        [DataMember]
        public string error;
        [DataMember]
        public string detail;
        [DataMember]
        public string lineId;
        [DataMember]
        public string source;
        [DataMember]
        public string stopSig;
        [DataMember]
        public ulong sourceRevision;
        [DataMember]
        public bool complete;
        [DataMember]
        public int prefixStopCount;
        [DataMember]
        public string missingKind;
        [DataMember]
        public DispatchWorkbenchRunChartSegmentDto[] segments;
        [DataMember]
        public DispatchWorkbenchRunChartDwellDto[] dwells;
    }

    [DataContract]
    public class RunTimeInvalidationDto
    {
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string source;
        [DataMember]
        public string reason;
    }

    [DataContract]
    public class DispatchWorkbenchRunTimeControlDto
    {
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string queryId;
    }

    [DataContract]
    public class DispatchWorkbenchRunTimeEditorDto
    {
        [DataMember]
        public string editorSessionId;
    }

    [DataContract]
    public class DispatchWorkbenchTimetableLineLayoutRequestDto
    {
        [DataMember]
        public string lineId;
    }

    [DataContract]
    public class DispatchWorkbenchTimetableLineStopDto
    {
        [DataMember]
        public int order;
        [DataMember]
        public string stopKey;
        [DataMember]
        public string name;
        [DataMember]
        public int waypointIndex;
    }

    [DataContract]
    public class DispatchWorkbenchTimetableLineLayoutDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public string lineId;
        [DataMember]
        public string mode;
        [DataMember]
        public string stopSig;
        [DataMember]
        public DispatchWorkbenchTimetableLineStopDto[] stops;
    }

    [DataContract]
    public class DispatchWorkbenchScheduleBatchRequestDto
    {
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public DispatchWorkbenchScheduleLineDto[] lines;
        [DataMember]
        public bool returnSnapshot = true;
    }

    [DataContract]
    public class DispatchWorkbenchScheduleLineDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string stopSig;
        [DataMember]
        public string runtimeResultId;
        [DataMember]
        public DispatchWorkbenchScheduleRowDto[] rows;
    }

    [DataContract]
    public class DispatchWorkbenchScheduleRowDto
    {
        [DataMember]
        public string rowId;
        [DataMember]
        public int slotMinute;
        [DataMember]
        public string kind;
        [DataMember]
        public string source;
        [DataMember]
        public DispatchWorkbenchTimedStopDto[] timedStops;
        [DataMember]
        public int truncateFromStopIndex = -1;
    }

    [DataContract]
    public class DispatchWorkbenchScheduleBatchResultDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string[] errors;
        [DataMember]
        public DispatchWorkbenchSnapshot snapshot;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartSectionRequestDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember(EmitDefaultValue = false)]
        public string sectionId;
        [DataMember]
        public ulong expectedIndexVersion;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartStationDirectoryRequestDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public ulong expectedIndexVersion;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartStationDirectoryResponseDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public string status;
        [DataMember]
        public ulong publishedIndexVersion;
        [DataMember]
        public DispatchWorkbenchRunChartStationDirectoryItemDto[] stations;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartStationDirectoryItemDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public string networkId;
        [DataMember]
        public string name;
        [DataMember]
        public bool passOnly;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartSectionResponseDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public ulong publishedIndexVersion;
        [DataMember]
        public string status;
        [DataMember]
        public DispatchWorkbenchRunChartSectionDto[] sections;
        [DataMember]
        public bool truncated;
        [DataMember]
        public string[] truncatedPairs;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartSectionDto
    {
        [DataMember]
        public string sectionId;
        [DataMember]
        public string mode;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto[] stations;
        [DataMember]
        public DispatchWorkbenchRunChartCoverageDto[] coverages;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartStationDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public int sectionIndex;
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public string type;
    }

    [DataContract]
    public class DispatchWorkbenchRunChartCoverageDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public string lineIdentity;
        [DataMember]
        public string mode;
        [DataMember]
        public int directionPhase;
        [DataMember]
        public ulong chainSignature;
        [DataMember]
        public ulong traversalSignature;
        [DataMember]
        public int fromSectionIndex;
        [DataMember]
        public int toSectionIndex;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto[] stops;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto[] passes;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto leadingStop;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto trailingStop;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorListRequestDto
    {
        [DataMember]
        public int dayOffset;
        [DataMember]
        public string lineId;
        [DataMember]
        public int startMinute;
        [DataMember]
        public int endMinute;
        [DataMember]
        public int limit;
        [DataMember]
        public DispatchWorkbenchMonitorFilterDto coverageFilter;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorFilterDto
    {
        [DataMember]
        public DispatchWorkbenchMonitorCoverageDto[] coverages;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorCoverageDto
    {
        [DataMember]
        public int fromSectionIndex;
        [DataMember]
        public int toSectionIndex;
        [DataMember]
        public DispatchWorkbenchRunChartStationDto[] points;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorListResponseDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public bool hasLineTrips;
        [DataMember]
        public bool dataComplete;
        [DataMember]
        public int droppedTripCount;
        [DataMember]
        public bool persistenceHealthy;
        [DataMember]
        public string lastIssueCode;
        [DataMember]
        public int issueCount;
        [DataMember]
        public int serviceDateKey;
        [DataMember]
        public int currentServiceDateKey;
        [DataMember]
        public int nowMinute;
        [DataMember]
        public long clockEpoch;
        [DataMember]
        public bool truncated;
        [DataMember]
        public DispatchWorkbenchMonitorSummaryDto summary;
        [DataMember]
        public DispatchWorkbenchMonitorTripHeaderDto[] trips = System.Array.Empty<DispatchWorkbenchMonitorTripHeaderDto>();
    }

    [DataContract]
    public class DispatchWorkbenchMonitorSummaryDto
    {
    }

    [DataContract]
    public class DispatchWorkbenchMonitorTripHeaderDto
    {
        [DataMember]
        public string tripKey;
        [DataMember]
        public string lineId;
        [DataMember]
        public int serviceDateKey;
        [DataMember]
        public int plannedStartMinute;
        [DataMember]
        public int? actualStartMinute;
        [DataMember]
        public int? plannedEndMinute;
        [DataMember]
        public int? actualEndMinute;
        [DataMember]
        public string scheduleType;
        [DataMember]
        public string state;
        [DataMember]
        public string endReason;
        [DataMember]
        public string serviceKind;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorDetailRequestDto
    {
        [DataMember]
        public string tripKey;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorDetailResponseDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public bool dataComplete;
        [DataMember]
        public int droppedTripCount;
        [DataMember]
        public bool persistenceHealthy;
        [DataMember]
        public string lastIssueCode;
        [DataMember]
        public int issueCount;
        [DataMember]
        public DispatchWorkbenchMonitorTripHeaderDto header;
        [DataMember]
        public DispatchWorkbenchMonitorStopDto[] stops = System.Array.Empty<DispatchWorkbenchMonitorStopDto>();
    }

    [DataContract]
    public class DispatchWorkbenchMonitorDetailsRequestDto
    {
        [DataMember]
        public string[] tripKeys;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorDetailsResponseDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public DispatchWorkbenchMonitorDetailResponseDto[] details = System.Array.Empty<DispatchWorkbenchMonitorDetailResponseDto>();
    }

    [DataContract]
    public class DispatchWorkbenchMonitorAverageStateDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string error;
        [DataMember]
        public string lineId;
        [DataMember]
        public string stopSig;
        [DataMember]
        public bool ready;
        [DataMember]
        public ulong revision;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorAverageRequestDto
    {
        [DataMember]
        public string editorSessionId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string stopSig;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorChangedDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public bool monitorAverageBecameReady;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorSubscriptionDto
    {
        [DataMember]
        public string averageWaitingLineId;
    }

    [DataContract]
    public class DispatchWorkbenchMonitorStopDto
    {
        [DataMember]
        public int order;
        [DataMember]
        public string stopKey;
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public int? plannedArrivalMinute;
        [DataMember]
        public int? plannedDepartureMinute;
        [DataMember]
        public int? actualArrivalMinute;
        [DataMember]
        public int? actualDepartureMinute;
        [DataMember]
        public bool skipped;
        [DataMember]
        public bool cleared;
    }
}
