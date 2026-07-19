using System.Runtime.Serialization;

namespace RapidTransitMod
{
    [DataContract]
    public class DispatchPlannerRequest
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string draftKey;
        [DataMember]
        public string analysisWindowId;
        [DataMember]
        public string windowStart;
        [DataMember]
        public string windowEnd;
        [DataMember]
        public string[] localLineIds;
        [DataMember]
        public string[] adjustableLineIds;
        [DataMember]
        public string expressSourceMode;
        [DataMember]
        public string expressLineId;
        [DataMember]
        public string virtualExpressBaseLineId;
        [DataMember]
        public string[] expressStopStationIds;
        [DataMember]
        public string departureMode;
        [DataMember]
        public int expressTripsPerHour;
        [DataMember]
        public int intervalMinutes;
        [DataMember]
        public string phaseTime;
        [DataMember]
        public int expressOffsetMinutes;
        [DataMember]
        public int maxOffsetMinutes;
        [DataMember]
        public int offsetStepMinutes;
        [DataMember]
        public int maxLocalRetimeMinutes;
        [DataMember]
        public int maxLocalWaitMinutes;
        [DataMember]
        public int maxAdditionalBypassStations;
        [DataMember]
        public string[] forcedBypassStationIds;
    }

    [DataContract]
    public class DispatchPlannerRequestEchoDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string draftKey;
        [DataMember]
        public string analysisWindowId;
        [DataMember]
        public string windowStart;
        [DataMember]
        public string windowEnd;
        [DataMember]
        public string[] localLineIds;
        [DataMember]
        public string[] adjustableLineIds;
        [DataMember]
        public string expressSourceMode;
        [DataMember]
        public string expressLineId;
        [DataMember]
        public string virtualExpressBaseLineId;
        [DataMember]
        public string[] expressStopStationIds;
        [DataMember]
        public string departureMode;
        [DataMember]
        public int expressTripsPerHour;
        [DataMember]
        public int intervalMinutes;
        [DataMember]
        public string phaseTime;
        [DataMember]
        public int expressOffsetMinutes;
        [DataMember]
        public int maxOffsetMinutes;
        [DataMember]
        public int offsetStepMinutes;
        [DataMember]
        public int maxLocalRetimeMinutes;
        [DataMember]
        public int maxLocalWaitMinutes;
        [DataMember]
        public int maxAdditionalBypassStations;
        [DataMember]
        public string[] forcedBypassStationIds;
    }

    [DataContract]
    public class DispatchPlannerInputSummaryDto
    {
        [DataMember]
        public string[] localLineIds;
        [DataMember]
        public string expressSourceCode;
        [DataMember]
        public string expressBaseLineId;
        [DataMember]
        public string[] expressStopStationIds;
        [DataMember]
        public int configuredBypassStationCount;
        [DataMember]
        public int candidateBypassStationCount;
        [DataMember]
        public int sharedCorridorCount;
        [DataMember]
        public int draftTripCount;
        [DataMember]
        public string[] effectiveLineIds;
        [DataMember]
        public string[] autoFixedConstraintLineIds;
        [DataMember]
        public int suppressedFixedVsFixedClusterCount;
        [DataMember]
        public int primaryRiskClusterCount;
    }

    [DataContract]
    public class DispatchPlannerDiagnosticDto
    {
        [DataMember]
        public string level;
        [DataMember]
        public string code;
        [DataMember]
        public string[] relatedClusterIds;
        [DataMember]
        public string[] lineIds;
        [DataMember]
        public string[] stationIds;
        [DataMember]
        public string[] tripIds;
        [DataMember]
        public float minutesA;
        [DataMember]
        public float minutesB;
        [DataMember]
        public int countA;
    }

    [DataContract]
    public class DispatchPlannerLineRoleDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public bool participates;
        [DataMember]
        public bool adjustable;
        [DataMember(Name = "fixed")]
        public bool fixedLine;
        [DataMember]
        public bool target;
    }

    [DataContract]
    public class DispatchPlannerLineRoleSummaryDto
    {
        [DataMember]
        public string[] effectiveLineIds;
        [DataMember]
        public string[] adjustableLineIds;
        [DataMember]
        public string[] fixedLineIds;
        [DataMember]
        public string[] targetLineIds;
        [DataMember]
        public string[] autoFixedConstraintLineIds;
        [DataMember]
        public int suppressedFixedVsFixedClusterCount;
        [DataMember]
        public DispatchPlannerLineRoleDto[] roles;
    }

    [DataContract]
    public class DispatchPlannerProblemIssueDto
    {
        [DataMember]
        public string type;
        [DataMember]
        public string severity;
        [DataMember]
        public string clusterId;
        [DataMember]
        public string catchupId;
        [DataMember]
        public string yieldingLineId;
        [DataMember]
        public string priorityLineId;
        [DataMember]
        public string yieldingTripId;
        [DataMember]
        public string priorityTripId;
        [DataMember]
        public float severityMinutes;
        [DataMember]
        public string recommendedBypassStationId;
        [DataMember]
        public float requiredHoldMinutes;
        [DataMember]
        public float holdBudgetMinutes;
        [DataMember]
        public float riskMinutes;
        [DataMember]
        public string[] lineIds;
    }

    [DataContract]
    public class DispatchPlannerPredictedHoldPairDto
    {
        [DataMember]
        public string catchupId;
        [DataMember]
        public string yieldingLineId;
        [DataMember]
        public string priorityLineId;
        [DataMember]
        public string yieldingTripId;
        [DataMember]
        public string priorityTripId;
        [DataMember]
        public string stationId;
        [DataMember]
        public string catchupTime;
        [DataMember]
        public float plannedHoldMinutes;
    }

    [DataContract]
    public class DispatchPlannerScheduleActionDto
    {
        [DataMember]
        public string actionType;
        [DataMember]
        public string type;
        [DataMember]
        public string shape;
        [DataMember]
        public string reason;
        [DataMember]
        public string[] targetRegionIds;
        [DataMember]
        public string[] reasonRegionIds;
        [DataMember]
        public string[] clusterIds;
        [DataMember]
        public string[] reasonClusterIds;
        [DataMember]
        public string[] stationIds;
        [DataMember]
        public string[] affectedLineIds;
        [DataMember]
        public string affectedLineId;
        [DataMember]
        public string[] affectedTripIds;
        [DataMember]
        public string[] priorityTripIds;
        [DataMember]
        public DispatchPlannerPredictedHoldPairDto[] predictedHoldPairs;
        [DataMember]
        public string[] tripIds;
        [DataMember]
        public float[] deltaPattern;
        [DataMember]
        public float deltaMinutes;
        [DataMember]
        public float deltaOffsetMinutes;
        [DataMember]
        public float riskScore;
    }

    [DataContract]
    public class DispatchPlannerIssueCountDto
    {
        [DataMember]
        public string type;
        [DataMember]
        public int count;
    }

    [DataContract]
    public class DispatchPlannerFrontendSummaryDto
    {
        [DataMember]
        public string[] effectiveLineIds;
        [DataMember]
        public string[] adjustableLineIds;
        [DataMember]
        public string[] fixedLineIds;
        [DataMember]
        public string[] targetLineIds;
        [DataMember]
        public string[] actuallyAdjustedLineIds;
        [DataMember]
        public DispatchPlannerIssueCountDto[] issueCountsByType;
        [DataMember]
        public int actionCount;
        [DataMember]
        public int catchupClusterCount;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
    }

    [DataContract]
    public class DispatchPlannerRiskItemDto
    {
        [DataMember]
        public string riskId;
        [DataMember]
        public string problemType;
        [DataMember]
        public string resolutionState;
        [DataMember]
        public string pairRole;
        [DataMember]
        public string treatmentType;
        [DataMember]
        public string blockReasonCode;
        [DataMember]
        public string[] suggestedOptionCodes;
        [DataMember]
        public string yieldingLineId;
        [DataMember]
        public string priorityLineId;
        [DataMember]
        public string yieldingTripId;
        [DataMember]
        public string priorityTripId;
        [DataMember]
        public string yieldingDepartTime;
        [DataMember]
        public string priorityDepartTime;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember]
        public string catchupFromStationId;
        [DataMember]
        public string catchupToStationId;
        [DataMember]
        public string catchupTime;
        [DataMember]
        public string selectedBypassStationId;
        [DataMember]
        public float requiredHoldMinutes;
        [DataMember]
        public float plannedAdjustmentMinutes;
        [DataMember]
        public float holdBudgetMinutes;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
        [DataMember]
        public float requiredMarginMinutes;
        [DataMember]
        public float currentWorstCaseGapMinutes;
    }

    [DataContract]
    public class DispatchPlannerPlanMetricsDto
    {
        [DataMember]
        public float expressSavedMinutes;
        [DataMember]
        public float localWaitMinutes;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
        [DataMember]
        public int addedBypassStationCount;
        [DataMember]
        public int retimedTripCount;
        [DataMember]
        public int recommendedExpressOffsetDeltaMinutes;
    }

    [DataContract]
    public class DispatchPlannerPlanSummaryDto
    {
        [DataMember]
        public string planId;
        [DataMember]
        public string objectiveId;
        [DataMember]
        public string status;
        [DataMember]
        public float score;
        [DataMember]
        public float expressSavedMinutes;
        [DataMember]
        public float localWaitMinutes;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
        [DataMember]
        public int addedBypassStationCount;
        [DataMember]
        public int retimedTripCount;
        [DataMember]
        public int recommendedExpressOffsetDeltaMinutes;
        [DataMember]
        public DispatchPlannerCapacityDiagnosticDto capacityDiagnostic;
    }

    [DataContract]
    public class DispatchPlannerCapacityDiagnosticDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string overallVerdict;
        [DataMember]
        public bool capacityLikely;
        [DataMember]
        public float minGapMinutes;
        [DataMember]
        public float highestCapacityConsumptionRatio;
        [DataMember]
        public float highestCapacityConsumptionPercent;
        [DataMember]
        public float highestCompressedSpanMinutes;
        [DataMember]
        public float highestZeroGapConsumptionRatio;
        [DataMember]
        public float requiredMaxShiftMinutes;
        [DataMember]
        public float requiredMaxWaitMinutes;
        [DataMember]
        public float minResidualSlackMinutes;
        [DataMember]
        public string criticalResourceId;
        [DataMember]
        public string criticalTargetLineId;
        [DataMember]
        public string[] criticalCoverageLineIds;
        [DataMember]
        public string[] criticalCoverageLines;
        [DataMember]
        public int criticalTargetStartAtomIndex;
        [DataMember]
        public int criticalTargetEndAtomIndexExclusive;
        [DataMember]
        public int tripCount;
        [DataMember]
        public int exportedSharedCorridorCount;
        [DataMember]
        public int validSharedCorridorCount;
        [DataMember]
        public int relevantSharedCorridorCount;
        [DataMember]
        public int projectedIntervalCount;
        [DataMember]
        public int elementarySectionCount;
        [DataMember]
        public int reportGroupCount;
        [DataMember]
        public string reason;
        [DataMember]
        public string summary;
    }

    [DataContract]
    public class DispatchPlannerRiskClusterDto
    {
        [DataMember]
        public string clusterId;
        [DataMember]
        public string severityLevel;
        [DataMember]
        public string yieldingLineId;
        [DataMember]
        public string priorityLineId;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember]
        public int catchupCount;
        [DataMember]
        public float maxSeverityMinutes;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
        [DataMember]
        public string recommendedBypassStationId;
        [DataMember]
        public string[] recommendedActionCodes;
        [DataMember]
        public DispatchPlannerRiskEventDto[] representativeEvents;
    }

    [DataContract]
    public class DispatchPlannerRiskEventDto
    {
        [DataMember]
        public string eventId;
        [DataMember]
        public string statusCode;
        [DataMember]
        public string reasonCode;
        [DataMember]
        public string problemType;
        [DataMember]
        public string resolutionState;
        [DataMember]
        public string pairRole;
        [DataMember]
        public string treatmentType;
        [DataMember]
        public string blockReasonCode;
        [DataMember]
        public string[] suggestedOptionCodes;
        [DataMember]
        public string yieldingLineId;
        [DataMember]
        public string priorityLineId;
        [DataMember]
        public string yieldingTripId;
        [DataMember]
        public string priorityTripId;
        [DataMember]
        public string yieldingDepartTime;
        [DataMember]
        public string priorityDepartTime;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember]
        public string catchupFromStationId;
        [DataMember]
        public string catchupToStationId;
        [DataMember]
        public string catchupTime;
        [DataMember]
        public float requiredHoldMinutes;
        [DataMember]
        public float plannedAdjustmentMinutes;
        [DataMember]
        public float holdBudgetMinutes;
        [DataMember]
        public float unresolvedRiskMinutes;
        [DataMember]
        public float robustnessRiskMinutes;
        [DataMember]
        public string selectedBypassStationId;
        [DataMember]
        public float requiredMarginMinutes;
        [DataMember]
        public float currentWorstCaseGapMinutes;
    }

    [DataContract]
    public class DispatchPlannerOptimizationRegionDto
    {
        [DataMember]
        public string regionId;
        [DataMember]
        public string[] clusterIds;
        [DataMember]
        public string[] yieldingLineIds;
        [DataMember]
        public string[] priorityLineIds;
        [DataMember]
        public int eventCount;
        [DataMember]
        public float firstCatchupMinute;
        [DataMember]
        public float lastCatchupMinute;
        [DataMember]
        public float totalUnresolvedRiskMinutes;
        [DataMember]
        public float totalRobustnessRiskMinutes;
    }

    [DataContract]
    public class DispatchPlannerPreviewRowDto
    {
        [DataMember]
        public string tripId;
        [DataMember]
        public string time;
        [DataMember]
        public string lineId;
        [DataMember]
        public string lineName;
        [DataMember]
        public string kind;
        [DataMember]
        public string originStationId;
        [DataMember]
        public string statusCode;
        [DataMember]
        public int deltaMinutes;
        [DataMember]
        public int statusMinutes;
    }

    [DataContract]
    public class DispatchPlannerChangedRowDto
    {
        [DataMember]
        public string tripId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string kind;
        [DataMember]
        public string beforeTime;
        [DataMember]
        public string afterTime;
        [DataMember]
        public int scheduleShiftMinutes;
        [DataMember]
        public int predictedDelayMinutes;
        [DataMember]
        public int totalDeltaMinutes;
        [DataMember]
        public string changeType;
        [DataMember]
        public string statusCode;
        [DataMember]
        public int statusMinutes;
    }

    [DataContract]
    public class DispatchPlannerChangedWindowDto
    {
        [DataMember]
        public string windowId;
        [DataMember]
        public string regionId;
        [DataMember]
        public string[] lineIds;
        [DataMember]
        public string[] lineNames;
        [DataMember]
        public string fromTime;
        [DataMember]
        public string toTime;
        [DataMember]
        public string[] changeTypes;
        [DataMember]
        public DispatchPlannerChangedRowDto[] rowDiffs;
    }

    [DataContract]
    public class DispatchPlannerPlanDetailDto
    {
        [DataMember]
        public string planId;
        [DataMember]
        public string objectiveId;
        [DataMember]
        public string status;
        [DataMember]
        public float score;
        [DataMember]
        public int recommendedExpressOffsetDeltaMinutes;
        [DataMember]
        public DispatchPlannerPlanMetricsDto metrics;
        [DataMember]
        public DispatchPlannerCapacityDiagnosticDto capacityDiagnostic;
        [DataMember]
        public string[] selectedBypassStationIds;
        [DataMember]
        public DispatchPlannerRiskClusterDto[] riskClusters;
        [DataMember]
        public DispatchPlannerRiskItemDto[] riskItems;
        [DataMember]
        public DispatchPlannerOptimizationRegionDto[] optimizationRegions;
        [DataMember]
        public DispatchPlannerScheduleActionDto[] structuredScheduleActions;
        [DataMember]
        public DispatchPlannerProblemIssueDto[] problemIssues;
        [DataMember]
        public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
        [DataMember]
        public DispatchPlannerFrontendSummaryDto frontendSummary;
        [DataMember]
        public DispatchPlannerPreviewRowDto[] timetablePreviewRows;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] plannerBaselineRows;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] plannerReplacementRows;
        [DataMember]
        public DispatchPlannerChangedWindowDto[] changedWindows;
        [DataMember]
        public DispatchPlannerDiagnosticDto[] diagnostics;
    }

    [DataContract]
    public class DispatchPlannerPerformanceDto
    {
        [DataMember]
        public string engineMode;
        [DataMember]
        public int localLineCount;
        [DataMember]
        public int expressLineCount;
        [DataMember]
        public int pursuitTrunkCount;
        [DataMember]
        public int rawCatchupEventCount;
        [DataMember]
        public int riskClusterCount;
        [DataMember]
        public int optimizationRegionCount;
    }

    [DataContract]
    public class DispatchPlannerResult
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string engineVersion;
        [DataMember]
        public DispatchPlannerRequestEchoDto requestEcho;
        [DataMember]
        public DispatchPlannerInputSummaryDto inputSummary;
        [DataMember]
        public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
        [DataMember]
        public DispatchPlannerCapacityDiagnosticDto baselineCapacityDiagnostic;
        [DataMember]
        public string defaultPlanId;
        [DataMember]
        public DispatchPlannerPlanDetailDto[] plans;
        [DataMember]
        public DispatchPlannerPlanSummaryDto[] planSummaries;
        [DataMember]
        public DispatchPlannerPlanDetailDto selectedPlan;
        [DataMember]
        public DispatchPlannerDiagnosticDto[] diagnostics;
        [DataMember]
        public DispatchPlannerPerformanceDto performance;
    }

    [DataContract]
    public class DispatchPlannerJobStatusDto
    {
        [DataMember]
        public string mode;
        [DataMember]
        public bool success;
        [DataMember]
        public string jobId;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
        [DataMember]
        public DispatchPlannerResult result;
    }

    [DataContract]
    public class DispatchPlannerExportSnapshot
    {
        [DataMember]
        public string mode;
        [DataMember]
        public string version;
        [DataMember]
        public uint generatedAtFrame;
        [DataMember]
        public DispatchPlannerLineDto[] lines;
        [DataMember]
        public DispatchPlannerStationDto[] stations;
        [DataMember]
        public DispatchPlannerSegmentDto[] segments;
        [DataMember]
        public DispatchPlannerBypassStationDto[] configuredBypassStations;
        [DataMember]
        public DispatchPlannerBypassStationDto[] candidateBypassStations;
        [DataMember]
        public DispatchPlannerTrackScenarioDto currentTrackScenario;
        [DataMember]
        public DispatchPlannerObservationSummaryDto observations;
        [DataMember]
        public DispatchPlannerRuntimeParamsDto runtimeParams;
        [DataMember]
        public DispatchPlannerDraftDto[] drafts;
    }

    [DataContract]
    public class DispatchPlannerLineDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public int entityIndex;
        [DataMember]
        public string name;
        [DataMember]
        public string kind;
        [DataMember]
        public string configuredKind;
        [DataMember]
        public string transportType;
        [DataMember]
        public int routeNumber;
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
        public string allowedDepotId;
        [DataMember]
        public bool hasTimeProfile;
        [DataMember]
        public float estimatedLoopMinutes;
        [DataMember]
        public DispatchPlannerOutsideEndpointDto[] outsideEndpoints;
    }

    [DataContract]
    public class DispatchPlannerOutsideEndpointDto
    {
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public string direction;
        [DataMember]
        public string kind;
        [DataMember]
        public int startLaneIndex;
        [DataMember]
        public int endLaneIndex;
        [DataMember]
        public float startCurvePos;
        [DataMember]
        public float endCurvePos;
    }

    [DataContract]
    public class DispatchPlannerStationDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string workbenchStationId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string name;
        [DataMember]
        public int order;
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public int trackAtomIndex;
        [DataMember]
        public int stopEntityIndex;
        [DataMember]
        public int buildingEntityIndex;
        [DataMember]
        public float distanceMeters;
        [DataMember]
        public float positionX;
        [DataMember]
        public float positionY;
        [DataMember]
        public float positionZ;
        [DataMember]
        public bool canConfigureBypass;
        [DataMember]
        public bool isConfiguredBypass;
        [DataMember]
        public float profileDwellMinutes;
        [DataMember]
        public float observedDwellMinutes;
        [DataMember]
        public int observedDwellSampleCount;
        [DataMember]
        public string dwellSource;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerSegmentDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember]
        public int fromOrder;
        [DataMember]
        public int toOrder;
        [DataMember]
        public int fromWaypointIndex;
        [DataMember]
        public int toWaypointIndex;
        [DataMember]
        public float distanceMeters;
        [DataMember]
        public float profileMinutes;
        [DataMember]
        public float estimatedMinutes;
        [DataMember]
        public string source;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerBypassStationDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public string workbenchStationId;
        [DataMember]
        public string lineId;
        [DataMember]
        public string name;
        [DataMember]
        public int order;
        [DataMember]
        public int buildingEntityIndex;
        [DataMember]
        public bool isConfigured;
        [DataMember]
        public bool isVirtualCandidate;
        [DataMember]
        public string reason;
    }

    [DataContract]
    public class DispatchPlannerTrackScenarioDto
    {
        [DataMember]
        public string scenarioId;
        [DataMember]
        public string scenarioType;
        [DataMember]
        public DispatchPlannerLineTrackDto[] lines;
        [DataMember]
        public DispatchPlannerSharedCorridorDto[] sharedCorridors;
        [DataMember]
        public int configuredBypassStationCount;
        [DataMember]
        public int candidateBypassStationCount;
        [DataMember]
        public int sharedCorridorCount;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerLineTrackDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public bool available;
        [DataMember]
        public string unavailableReason;
        [DataMember]
        public string chainSignature;
        [DataMember]
        public int trackAtomCount;
        [DataMember]
        public int controlPointCount;
        [DataMember]
        public int sharedRunCount;
        [DataMember]
        public int protectedIntervalCount;
        [DataMember]
        public int protectedSharedIntervalCount;
        [DataMember]
        public string executionMode;
        [DataMember]
        public DispatchPlannerProtectedIntervalDto[] protectedIntervals;
        [DataMember]
        public DispatchPlannerTraversalSliceDto[] traversalSlices;
        [DataMember]
        public DispatchPlannerTrackAtomDto[] trackAtoms;
    }

    [DataContract]
    public class DispatchPlannerTrackAtomDto
    {
        [DataMember]
        public int atomIndex;
        [DataMember]
        public int sourceTargetEntityIndex;
        [DataMember]
        public int physicalLaneEntityIndex;
        [DataMember]
        public float targetDeltaStart;
        [DataMember]
        public float targetDeltaEnd;
        [DataMember]
        public string sourceFlags;
        [DataMember]
        public string atomClass;
        [DataMember]
        public string traversalDir;
        [DataMember]
        public bool hasCurve;
        [DataMember]
        public int curveEntityIndex;
        [DataMember]
        public float curveLengthMeters;
        [DataMember]
        public float traversalLengthMeters;
        [DataMember]
        public float bezierAx;
        [DataMember]
        public float bezierAy;
        [DataMember]
        public float bezierAz;
        [DataMember]
        public float bezierBx;
        [DataMember]
        public float bezierBy;
        [DataMember]
        public float bezierBz;
        [DataMember]
        public float bezierCx;
        [DataMember]
        public float bezierCy;
        [DataMember]
        public float bezierCz;
        [DataMember]
        public float bezierDx;
        [DataMember]
        public float bezierDy;
        [DataMember]
        public float bezierDz;
        [DataMember]
        public bool hasTrackLane;
        [DataMember]
        public float speedLimitMetersPerSecond;
        [DataMember]
        public float curviness;
        [DataMember]
        public string trackLaneFlags;
        [DataMember]
        public int trackLaneFlagsRaw;
    }

    [DataContract]
    public class DispatchPlannerProtectedIntervalDto
    {
        [DataMember]
        public int intervalIndex;
        [DataMember]
        public string fromStationId;
        [DataMember]
        public string toStationId;
        [DataMember]
        public int fromBuildingEntityIndex;
        [DataMember]
        public int toBuildingEntityIndex;
        [DataMember]
        public int startControlPointIndex;
        [DataMember]
        public int endControlPointIndex;
        [DataMember]
        public int startAtomIndex;
        [DataMember]
        public int endAtomIndexExclusive;
        [DataMember]
        public float baseMinutes;
        [DataMember]
        public int sharedSegmentCount;
        [DataMember]
        public int maxSharedLineCount;
        [DataMember]
        public bool hasMirroredContext;
        [DataMember]
        public float minEntryOffsetMinutes;
        [DataMember]
        public float maxClearOffsetMinutes;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerTraversalSliceDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public int sliceIndex;
        [DataMember]
        public int startAtomIndex;
        [DataMember]
        public int endAtomIndexExclusive;
        [DataMember]
        public int physicalLaneCount;
        [DataMember]
        public string startEventKind;
        [DataMember]
        public string endEventKind;
        [DataMember]
        public int startWaypointIndex;
        [DataMember]
        public int endWaypointIndex;
        [DataMember]
        public string stationTraversalKind;
        [DataMember]
        public int stationWaypointIndex;
        [DataMember]
        public float stationStopMinutes;
        [DataMember]
        public bool observedIncludesStationStop;
        [DataMember]
        public float modelRunMinutes;
        [DataMember]
        public float observedAverageMinutes;
        [DataMember]
        public float observedFastMinutes;
        [DataMember]
        public int observedSampleCount;
        [DataMember]
        public uint lastObservedFrame;
        [DataMember]
        public string source;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerSharedCorridorDto
    {
        [DataMember]
        public string id;
        [DataMember]
        public string lineId;
        [DataMember]
        public string otherLineId;
        [DataMember]
        public int lineStartAtomIndex;
        [DataMember]
        public int lineEndAtomIndexExclusive;
        [DataMember]
        public int otherStartAtomIndex;
        [DataMember]
        public int otherEndAtomIndexExclusive;
        [DataMember]
        public string lineStartStationId;
        [DataMember]
        public string lineEndStationId;
        [DataMember]
        public string otherStartStationId;
        [DataMember]
        public string otherEndStationId;
        [DataMember]
        public int lineSharedSliceCount;
        [DataMember]
        public int otherSharedSliceCount;
        [DataMember]
        public int lineBridgedGapAtoms;
        [DataMember]
        public int otherBridgedGapAtoms;
        [DataMember]
        public int physicalOverlap;
        [DataMember]
        public int orderedRun;
        [DataMember]
        public bool hasMirroredContext;
        [DataMember]
        public int maxSharedLineCount;
        [DataMember]
        public string traversalRelation;
        [DataMember]
        public bool hasCanonicalDirection;
        [DataMember]
        public bool lineAlongCanonical;
        [DataMember]
        public bool otherAlongCanonical;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerObservationSummaryDto
    {
        [DataMember]
        public int stopDwellObservationCount;
        [DataMember]
        public int stopDwellSampleCount;
        [DataMember]
        public int traversalSliceObservationCount;
        [DataMember]
        public int traversalSliceSampleCount;
        [DataMember]
        public DispatchPlannerStationDwellObservationDto[] stopDwell;
        [DataMember]
        public DispatchPlannerTraversalSliceDto[] traversalSlices;
        [DataMember]
        public DispatchPlannerTraversalSliceActualSampleDto[] traversalSliceActualSamples;
        [DataMember]
        public DispatchPlannerTraversalPositionSampleDto[] traversalPositionSamples;
    }

    [DataContract]
    public class DispatchPlannerTraversalSliceActualSampleDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public int lineEntityIndex;
        [DataMember]
        public int vehicleEntityIndex;
        [DataMember]
        public int sliceIndex;
        [DataMember]
        public uint enterFrame;
        [DataMember]
        public uint exitFrame;
        [DataMember]
        public float durationMinutes;
        [DataMember]
        public int enterAtomIndex;
        [DataMember]
        public float enterAtomPosition01;
        [DataMember]
        public int exitAtomIndex;
        [DataMember]
        public float exitAtomPosition01;
    }

    [DataContract]
    public class DispatchPlannerTraversalPositionSampleDto
    {
        [DataMember]
        public string lineId;
        [DataMember]
        public int lineEntityIndex;
        [DataMember]
        public int vehicleEntityIndex;
        [DataMember]
        public uint frame;
        [DataMember]
        public int sliceIndex;
        [DataMember]
        public int segmentIndex;
        [DataMember]
        public float segmentPosition;
        [DataMember]
        public int atomIndex;
        [DataMember]
        public float atomPosition01;
        [DataMember]
        public int physicalLaneEntityIndex;
        [DataMember]
        public float speedMetersPerSecond;
        [DataMember]
        public float odometerMeters;
    }

    [DataContract]
    public class DispatchPlannerStationDwellObservationDto
    {
        [DataMember]
        public string stationId;
        [DataMember]
        public string lineId;
        [DataMember]
        public int waypointIndex;
        [DataMember]
        public float averageMinutes;
        [DataMember]
        public int sampleCount;
        [DataMember]
        public string source;
        [DataMember]
        public float confidence;
    }

    [DataContract]
    public class DispatchPlannerRuntimeParamsDto
    {
        [DataMember]
        public double simFramesPerMinute;
        [DataMember]
        public long clockEpoch;
        [DataMember]
        public int defaultOriginHoldLimitMinutes;
        [DataMember]
        public int defaultMaxStationDwellMinutes;
        [DataMember]
        public float trackModelEntryClearSafetyGapMinutes;
        [DataMember]
        public float localBypassExitReleaseAtoms;
        [DataMember]
        public float localBypassTrainTailClearAtoms;
        [DataMember]
        public int minStrongProtectedIntervalOverlapAtoms;
        [DataMember]
        public int minStrongProtectedIntervalOrderedRun;
        [DataMember]
        public string compatibilityMode;
    }

    [DataContract]
    public class DispatchPlannerDraftDto
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
        public DispatchWorkbenchStagedRowDto[] lineDraftRows;
        [DataMember]
        public DispatchWorkbenchStagedRowDto[] stagedRows;
        [DataMember(EmitDefaultValue = false)]
        public DispatchWorkbenchAutoRuleDto[] autoRules;
        [DataMember]
        public DispatchWorkbenchTripDto[] trips;
    }
}
