using System.Runtime.Serialization;

namespace RapidTransitMod.Dispatch.Observation
{
    [DataContract]
    internal class SnapshotDto
    {
        [DataMember] public int schemaVersion;
        [DataMember] public string snapshotId;
        [DataMember] public string status;
        [DataMember] public uint generatedAtFrame;
        [DataMember] public uint appliedAtFrame;
        [DataMember] public uint lastUpdatedFrame;
        [DataMember] public TripDto[] appliedTrips;
        [DataMember] public StopDto[] stopEvents;
        [DataMember] public BypassDto[] bypassEvents;
        [DataMember] public CorridorDto[] corridorPassages;
        [DataMember] public BaselineDto[] baselineRows;
        [DataMember] public ContractDto[] plannerContracts;
        [DataMember] public ReportDto attainmentReport;
    }

    [DataContract]
    internal class BaselineDto
    {
        [DataMember] public string draftKey;
        [DataMember] public string rowId;
        [DataMember] public string lineId;
        [DataMember] public string plannedTime;
        [DataMember] public int plannedMinute;
        [DataMember] public string serviceKind;
        [DataMember] public string source;
        [DataMember] public string originStationId;
        [DataMember] public string originStationName;
    }

    [DataContract]
    internal class ContractDto
    {
        [DataMember] public string draftKey;
        [DataMember] public string importedFrom;
        [DataMember] public string importedPlanId;
        [DataMember] public string importedObjectiveId;
        [DataMember] public string[] importedLineIds;
        [DataMember] public EchoDto requestEcho;
        [DataMember] public RoleSummaryDto lineRoleSummary;
        [DataMember] public string[] selectedBypassStationIds;
        [DataMember] public ChangeDto[] changedRows;
        [DataMember] public ActionDto[] structuredActions;
        [DataMember] public RiskDto[] riskItems;
    }

    [DataContract]
    internal class EchoDto
    {
        [DataMember] public string draftKey;
        [DataMember] public string analysisWindowId;
        [DataMember] public string windowStart;
        [DataMember] public string windowEnd;
        [DataMember] public string[] localLineIds;
        [DataMember] public string[] adjustableLineIds;
        [DataMember] public string expressSourceMode;
        [DataMember] public string expressLineId;
        [DataMember] public string virtualExpressBaseLineId;
        [DataMember] public string[] expressStopStationIds;
        [DataMember] public string departureMode;
        [DataMember] public int expressTripsPerHour;
        [DataMember] public int intervalMinutes;
        [DataMember] public string phaseTime;
        [DataMember] public int expressOffsetMinutes;
        [DataMember] public int maxOffsetMinutes;
        [DataMember] public int offsetStepMinutes;
        [DataMember] public int maxLocalRetimeMinutes;
        [DataMember] public int maxLocalWaitMinutes;
        [DataMember] public int maxAdditionalBypassStations;
        [DataMember] public string[] forcedBypassStationIds;
    }

    [DataContract]
    internal class RoleSummaryDto
    {
        [DataMember] public string[] effectiveLineIds;
        [DataMember] public string[] adjustableLineIds;
        [DataMember] public string[] fixedLineIds;
        [DataMember] public string[] targetLineIds;
        [DataMember] public string[] autoFixedConstraintLineIds;
        [DataMember] public int suppressedFixedVsFixedClusterCount;
        [DataMember] public RoleDto[] roles;
    }

    [DataContract]
    internal class RoleDto
    {
        [DataMember] public string lineId;
        [DataMember] public bool participates;
        [DataMember] public bool adjustable;
        [DataMember(Name = "fixed")] public bool fixedLine;
        [DataMember] public bool target;
    }

    [DataContract]
    internal class ChangeDto
    {
        [DataMember] public string tripId;
        [DataMember] public string lineId;
        [DataMember] public string kind;
        [DataMember] public string beforeTime;
        [DataMember] public string afterTime;
        [DataMember] public int scheduleShiftMinutes;
        [DataMember] public int predictedDelayMinutes;
        [DataMember] public int totalDeltaMinutes;
        [DataMember] public string changeType;
        [DataMember] public string statusCode;
        [DataMember] public int statusMinutes;
    }

    [DataContract]
    internal class ActionDto
    {
        [DataMember] public string actionType;
        [DataMember] public string type;
        [DataMember] public string shape;
        [DataMember] public string reason;
        [DataMember] public string[] targetRegionIds;
        [DataMember] public string[] reasonRegionIds;
        [DataMember] public string[] clusterIds;
        [DataMember] public string[] reasonClusterIds;
        [DataMember] public string[] stationIds;
        [DataMember] public string[] affectedLineIds;
        [DataMember] public string affectedLineId;
        [DataMember] public string[] affectedTripIds;
        [DataMember] public string[] priorityTripIds;
        [DataMember] public string[] tripIds;
        [DataMember] public float[] deltaPattern;
        [DataMember] public float deltaMinutes;
        [DataMember] public float deltaOffsetMinutes;
        [DataMember] public float riskScore;
    }

    [DataContract]
    internal class RiskDto
    {
        [DataMember] public string riskId;
        [DataMember] public string problemType;
        [DataMember] public string resolutionState;
        [DataMember] public string pairRole;
        [DataMember] public string treatmentType;
        [DataMember] public string blockReasonCode;
        [DataMember] public string[] suggestedOptionCodes;
        [DataMember] public string yieldingLineId;
        [DataMember] public string priorityLineId;
        [DataMember] public string yieldingTripId;
        [DataMember] public string priorityTripId;
        [DataMember] public string yieldingDepartTime;
        [DataMember] public string priorityDepartTime;
        [DataMember] public string fromStationId;
        [DataMember] public string toStationId;
        [DataMember] public string catchupFromStationId;
        [DataMember] public string catchupToStationId;
        [DataMember] public string catchupTime;
        [DataMember] public string selectedBypassStationId;
        [DataMember] public float requiredHoldMinutes;
        [DataMember] public float plannedAdjustmentMinutes;
        [DataMember] public float holdBudgetMinutes;
        [DataMember] public float unresolvedRiskMinutes;
        [DataMember] public float robustnessRiskMinutes;
        [DataMember] public float requiredMarginMinutes;
        [DataMember] public float currentWorstCaseGapMinutes;
    }

    [DataContract]
    internal class ReportDto
    {
        [DataMember] public ReportSummaryDto summary;
        [DataMember] public TripResultDto[] tripResults;
        [DataMember] public ActionResultDto[] actionResults;
    }

    [DataContract]
    internal class ReportSummaryDto
    {
        [DataMember] public int baselineTripCount;
        [DataMember] public int observedTripCount;
        [DataMember] public int launchedTripCount;
        [DataMember] public int missingTripCount;
        [DataMember] public int plannerContractCount;
        [DataMember] public int plannerChangedTripCount;
        [DataMember] public int plannerActionCount;
        [DataMember] public int satisfiedActionCount;
        [DataMember] public int unresolvedActionCount;
    }

    [DataContract]
    internal class TripResultDto
    {
        [DataMember] public string draftKey;
        [DataMember] public string rowId;
        [DataMember] public string lineId;
        [DataMember] public string plannedTime;
        [DataMember] public int plannedMinute;
        [DataMember] public string serviceDate;
        [DataMember] public int serviceDayIndex;
        [DataMember] public int occurrenceIndex;
        [DataMember] public string actualDepartureTime;
        [DataMember] public int actualDepartureMinute;
        [DataMember] public int deltaMinutes;
        [DataMember] public string serviceKind;
        [DataMember] public string source;
        [DataMember] public string state;
        [DataMember] public string bindingConfidence;
        [DataMember] public string reasonCode;
        [DataMember] public string matchMode;
        [DataMember] public string contractPlanId;
        [DataMember] public string contractTripId;
    }

    [DataContract]
    internal class ActionResultDto
    {
        [DataMember] public string contractPlanId;
        [DataMember] public string actionId;
        [DataMember] public string actionType;
        [DataMember] public string[] lineIds;
        [DataMember] public string[] tripRowIds;
        [DataMember] public string[] stationIds;
        [DataMember] public float expectedMinutes;
        [DataMember] public float actualMinutes;
        [DataMember] public string status;
        [DataMember] public string reason;
    }

    [DataContract]
    internal class TripDto
    {
        [DataMember] public string tripObservationId;
        [DataMember] public string state;
        [DataMember] public string lineId;
        [DataMember] public string rowId;
        [DataMember] public string source;
        [DataMember] public string serviceKind;
        [DataMember] public string plannedTime;
        [DataMember] public string serviceDate;
        [DataMember] public int serviceDayIndex;
        [DataMember] public int occurrenceIndex;
        [DataMember] public string actualDepartureTime;
        [DataMember] public int targetMinute;
        [DataMember] public int actualDepartureMinute;
        [DataMember] public int deltaMinutes;
        [DataMember] public int vehicleIndex;
        [DataMember] public uint launchFrame;
        [DataMember] public string bindingConfidence;
        [DataMember] public string reasonCode;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    internal class StopDto
    {
        [DataMember] public string eventId;
        [DataMember] public string eventType;
        [DataMember] public string tripObservationId;
        [DataMember] public string rowId;
        [DataMember] public string lineId;
        [DataMember] public string serviceDate;
        [DataMember] public int serviceDayIndex;
        [DataMember] public int occurrenceIndex;
        [DataMember] public int vehicleIndex;
        [DataMember] public int targetMinute;
        [DataMember] public string stationId;
        [DataMember] public string plannerStationId;
        [DataMember] public string stationName;
        [DataMember] public int waypointIndex;
        [DataMember] public bool isOrigin;
        [DataMember] public string arrivalTime;
        [DataMember] public string departureTime;
        [DataMember] public uint arrivalFrame;
        [DataMember] public uint departureFrame;
        [DataMember] public float dwellMinutes;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    internal class BypassDto
    {
        [DataMember] public string eventId;
        [DataMember] public string state;
        [DataMember] public string localTripObservationId;
        [DataMember] public string localRowId;
        [DataMember] public string localServiceDate;
        [DataMember] public int localServiceDayIndex;
        [DataMember] public int localOccurrenceIndex;
        [DataMember] public string priorityTripObservationId;
        [DataMember] public string priorityRowId;
        [DataMember] public string priorityServiceDate;
        [DataMember] public int priorityServiceDayIndex;
        [DataMember] public int priorityOccurrenceIndex;
        [DataMember] public string localLineId;
        [DataMember] public string priorityLineId;
        [DataMember] public int localVehicleIndex;
        [DataMember] public int priorityVehicleIndex;
        [DataMember] public int localTargetMinute;
        [DataMember] public int priorityTargetMinute;
        [DataMember] public string holdStationId;
        [DataMember] public string holdPlannerStationId;
        [DataMember] public string holdStationName;
        [DataMember] public int waypointIndex;
        [DataMember] public uint holdStartFrame;
        [DataMember] public uint holdReleaseFrame;
        [DataMember] public float actualHoldMinutes;
        [DataMember] public string decisionReason;
        [DataMember] public string releaseReason;
        [DataMember] public string sceneKey;
        [DataMember] public int protectedIntervalIndex;
        [DataMember] public uint lastUpdatedFrame;
    }

    [DataContract]
    internal class CorridorDto
    {
        [DataMember] public string passageId = string.Empty;
        [DataMember] public string tripObservationId = string.Empty;
        [DataMember] public string rowId = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string serviceDate = string.Empty;
        [DataMember] public int serviceDayIndex = -1;
        [DataMember] public int occurrenceIndex = 1;
        [DataMember] public int vehicleIndex = -1;
        [DataMember] public int targetMinute = -1;
        [DataMember] public string corridorId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public uint entryFrame = 0;
        [DataMember] public uint exitFrame = 0;
        [DataMember] public string entryTime = string.Empty;
        [DataMember] public string exitTime = string.Empty;
        [DataMember] public int entryAtomIndex = -1;
        [DataMember] public int exitAtomIndex = -1;
    }
}
