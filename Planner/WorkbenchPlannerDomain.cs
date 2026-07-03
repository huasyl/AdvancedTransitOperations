using System;
using System.Collections.Generic;

namespace RapidTransitMod.Planner
{
    internal static class PlannerDefaults
    {
        public const string EngineVersion = "csharp-planner-v1";
        public const string ObjectiveBalanced = "balanced";
        public const string ObjectiveFastestExpress = "fastestExpress";
        public const string ObjectiveMinBypassStations = "minBypassStations";
        public const string ObjectiveMaxSystemEfficiency = "maxSystemEfficiency";
        public const float StopStartLossMinutesPerSkippedStop = 3f;
        public const int ObservedStationRuntimeMinSamples = 2;
        public const float ObservedStationRuntimeMaxProfileRatio = 1.8f;
        public const float ObservedStationRuntimeMaxProfileExtraMinutes = 12f;
        public const int PursuitTrunkMergeGapAtoms = 12;
        public const int BypassStationEndpointToleranceAtoms = 8;
        public const float PursuitCurveSampleStepMinutes = 2f;
        public const float MinSharedGapMinutes = 1f;
        public const float RobustnessMarginTargetMinutes = 5f;
        public const int DefaultMinDepartureGapMinutes = 5;

        public static readonly PlannerObjectiveDefinition[] Objectives =
        {
            new PlannerObjectiveDefinition(
                ObjectiveBalanced,
                1.0f,
                0.7f,
                1.4f,
                0.9f,
                0.2f),
            new PlannerObjectiveDefinition(
                ObjectiveFastestExpress,
                1.4f,
                0.45f,
                1.2f,
                0.65f,
                0.05f),
            new PlannerObjectiveDefinition(
                ObjectiveMinBypassStations,
                0.9f,
                0.9f,
                1.5f,
                1.0f,
                0.8f),
            new PlannerObjectiveDefinition(
                ObjectiveMaxSystemEfficiency,
                1.1f,
                1.0f,
                1.5f,
                1.1f,
                0.35f)
        };
    }

    internal sealed class PlannerObjectiveDefinition
    {
        public PlannerObjectiveDefinition(
            string id,
            float expressBenefitWeight,
            float localWaitWeight,
            float unresolvedRiskWeight,
            float robustnessRiskWeight,
            float bypassStationWeight)
        {
            Id = id ?? string.Empty;
            ExpressBenefitWeight = expressBenefitWeight;
            LocalWaitWeight = localWaitWeight;
            UnresolvedRiskWeight = unresolvedRiskWeight;
            RobustnessRiskWeight = robustnessRiskWeight;
            BypassStationWeight = bypassStationWeight;
        }

        public string Id;
        public float ExpressBenefitWeight;
        public float LocalWaitWeight;
        public float UnresolvedRiskWeight;
        public float RobustnessRiskWeight;
        public float BypassStationWeight;
    }

    internal sealed class PlannerValidationIssue
    {
        public string Level = "info";
        public string Code = string.Empty;
        public string Message = string.Empty;
        public string[] RelatedClusterIds = new string[0];
        public string[] LineIds = new string[0];
        public string[] StationIds = new string[0];
        public string[] TripIds = new string[0];
        public float MinutesA = 0f;
        public float MinutesB = 0f;
        public int CountA = 0;
    }

    internal sealed class PlannerObservedRuntimeSummary
    {
        public float Minutes = 0f;
        public float MedianMinutes = 0f;
        public float AverageMinutes = 0f;
        public float MinMinutes = 0f;
        public float MaxMinutes = 0f;
        public float Confidence = 0f;
        public float VariabilityMinutes = 0f;
        public int SampleCount = 0;
        public string Source = "tripObserved";
        public string BaselinePolicy = "fastObservedQuartile";
    }

    internal sealed class PlannerWorkingRow
    {
        public string Id = string.Empty;
        public string LineId = string.Empty;
        public string Kind = "local";
        public int Minute = 0;
        public string Source = string.Empty;
        public string Note = string.Empty;
    }

    internal sealed class PlannerStationOffset
    {
        public string StationId = string.Empty;
        public int Order = 0;
        public string Name = string.Empty;
        public float ArrivalMinute = 0f;
        public float DepartureMinute = 0f;
        public float DwellMinutes = 0f;
        public float SkippedStopStartLossMinutes = 0f;
        public float VariabilityMinutes = 0f;
        public float Confidence = 0f;
        public string DwellSource = string.Empty;
        public bool ShouldStop = true;
    }

    internal sealed class PlannerSegmentRuntime
    {
        public string FromStationId = string.Empty;
        public string ToStationId = string.Empty;
        public int FromOrder = 0;
        public int ToOrder = 0;
        public float Minutes = 0f;
        public float MedianMinutes = 0f;
        public float AverageMinutes = 0f;
        public float MinMinutes = 0f;
        public float MaxMinutes = 0f;
        public float Confidence = 0f;
        public float VariabilityMinutes = 0f;
        public int SampleCount = 0;
        public string Source = string.Empty;
        public string BaselinePolicy = string.Empty;
    }

    internal sealed class PlannerStationEvent
    {
        public string StationId = string.Empty;
        public int Order = 0;
        public string Name = string.Empty;
        public float ArrivalMinute = 0f;
        public float DepartureMinute = 0f;
        public float DwellMinutes = 0f;
        public float SkippedStopStartLossMinutes = 0f;
    }

    internal sealed class PlannerTripModel
    {
        public string TripId = string.Empty;
        public string LineId = string.Empty;
        public string Kind = "local";
        public int DepartureMinute = 0;
        public string DepartureTime = string.Empty;
        public string Source = string.Empty;
        public string Note = string.Empty;
        public List<PlannerStationEvent> StationEvents = new List<PlannerStationEvent>();
        public float[] AtomBoundaryMinuteOffsets = new float[0];
        public float[] AtomBoundaryVariabilityOffsets = new float[0];
        public List<PlannerTripHoldSegment> HoldSegments = new List<PlannerTripHoldSegment>();
    }

    internal sealed class PlannerTripHoldSegment
    {
        public string StationId = string.Empty;
        public int StationOrder = 0;
        public int DepartureBoundaryAtomIndex = -1;
        public float DelayMinutes = 0f;
    }

    internal sealed class PlannerLineRuntimeModel
    {
        public string LineId = string.Empty;
        public string SourceLineId = string.Empty;
        public string LineName = string.Empty;
        public string Kind = string.Empty;
        public int StationCount = 0;
        public int TrackAtomCount = 0;
        public float TotalMinuteSpan = 0f;
        public DispatchPlannerLineDto Line;
        public DispatchPlannerLineTrackDto LineTrack;
        public List<DispatchPlannerStationDto> Stations = new List<DispatchPlannerStationDto>();
        public List<DispatchPlannerSegmentDto> Segments = new List<DispatchPlannerSegmentDto>();
        public List<PlannerStationOffset> StationOffsets = new List<PlannerStationOffset>();
        public Dictionary<string, PlannerStationOffset> StationOffsetsById = new Dictionary<string, PlannerStationOffset>(StringComparer.Ordinal);
        public List<PlannerSegmentRuntime> SegmentRuntimeOffsets = new List<PlannerSegmentRuntime>();
        public Dictionary<string, PlannerSegmentRuntime> SegmentRuntimeByStationPair = new Dictionary<string, PlannerSegmentRuntime>(StringComparer.Ordinal);
        public float[] AtomBoundaryMinuteOffsets = new float[0];
        public float[] AtomBoundaryVariabilityOffsets = new float[0];
    }

    internal sealed class PlannerRuntimeCatalog
    {
        public Dictionary<string, PlannerLineRuntimeModel> ModelsByLineId = new Dictionary<string, PlannerLineRuntimeModel>(StringComparer.Ordinal);
    }

    internal sealed class PlannerBypassStation
    {
        public string StationId = string.Empty;
        public string WorkbenchStationId = string.Empty;
        public string LineId = string.Empty;
        public string Name = string.Empty;
        public int Order = 0;
        public int TrackAtomIndex = -1;
        public bool IsConfigured = false;
        public bool IsVirtualCandidate = false;
    }

    internal sealed class PlannerBypassEvaluation
    {
        public string StationId = string.Empty;
        public string Name = string.Empty;
        public int Order = 0;
        public bool IsConfigured = false;
        public bool IsVirtualCandidate = false;
        public int AxisIndex = -1;
        public float GapAtStationMinutes = 0f;
        public float HoldNeededMinutes = 0f;
        public float RobustnessHoldNeededMinutes = 0f;
        public float TargetHoldMinutes = 0f;
        public float LocalStationMinute = 0f;
        public float ExpressStationMinute = 0f;
        public float StationDepartureMinute = 0f;
        public int DepartureBoundaryAtomIndex = -1;
    }

    internal sealed class PursuitTrunk
    {
        public string TrunkId = string.Empty;
        public string LocalLineId = string.Empty;
        public string ExpressLineId = string.Empty;
        public string PairRole = string.Empty;
        public bool IsPrimaryPlanningRisk = false;
        public bool IsSuppressed = false;
        public string YieldingLineId = string.Empty;
        public string PriorityLineId = string.Empty;
        public string FromStationId = string.Empty;
        public string ToStationId = string.Empty;
        public string ExpressFromStationId = string.Empty;
        public string ExpressToStationId = string.Empty;
        public int LocalStartAtomIndex = -1;
        public int LocalEndAtomIndexExclusive = -1;
        public int ExpressStartAtomIndex = -1;
        public int ExpressEndAtomIndexExclusive = -1;
        public float LocalEntryOffsetMinutes = 0f;
        public float LocalExitOffsetMinutes = 0f;
        public float ExpressEntryOffsetMinutes = 0f;
        public float ExpressExitOffsetMinutes = 0f;
        public float LocalRuntimeMinutes = 0f;
        public float ExpressRuntimeMinutes = 0f;
        public int AxisSampleCount = 0;
        public int SourceCorridorCount = 0;
        public float Confidence = 0f;
        public List<string> SourceCorridorIds = new List<string>();
    }

    internal sealed class PlannerCatchupEvent
    {
        public string EventId = string.Empty;
        public string LocalTripId = string.Empty;
        public string ExpressTripId = string.Empty;
        public string YieldingTripId = string.Empty;
        public string PriorityTripId = string.Empty;
        public string LocalLineId = string.Empty;
        public string ExpressLineId = string.Empty;
        public string PairRole = string.Empty;
        public string ProblemType = string.Empty;
        public string ResolutionState = string.Empty;
        public string TreatmentType = string.Empty;
        public string BlockReasonCode = string.Empty;
        public string[] SuggestedOptionCodes = new string[0];
        public string YieldingLineId = string.Empty;
        public string PriorityLineId = string.Empty;
        public string TrunkId = string.Empty;
        public string FromStationId = string.Empty;
        public string ToStationId = string.Empty;
        public string CatchupFromStationId = string.Empty;
        public string CatchupToStationId = string.Empty;
        public float LocalEntryMinute = 0f;
        public float ExpressEntryMinute = 0f;
        public float LocalExitMinute = 0f;
        public float ExpressExitMinute = 0f;
        public float GapAtEntryMinutes = 0f;
        public float GapAtExitMinutes = 0f;
        public float ClosingMinutes = 0f;
        public float MinSharedGapMinutes = 0f;
        public float MinGapMinutes = 0f;
        public float MinGapUncertaintyMinutes = 0f;
        public float WorstCaseGapMinutes = 0f;
        public float SeverityMinutes = 0f;
        public float UnresolvedRiskMinutes = 0f;
        public float RobustnessRiskMinutes = 0f;
        public float RequiredHoldMinutes = 0f;
        public float RequiredMarginMinutes = 0f;
        public float CurrentWorstCaseGapMinutes = 0f;
        public float HoldBudgetMinutes = 0f;
        public float ResolvedHoldMinutes = 0f;
        public float ExpressSavedMinutes = 0f;
        public float CatchupMinute = 0f;
        public int CatchupAxisIndex = -1;
        public bool DidCatchUp = false;
        public bool WithinHoldBudget = false;
        public float Confidence = 0f;
        public PlannerBypassEvaluation SelectedBypassStation;
        public List<PlannerBypassStation> UsableBypassStations = new List<PlannerBypassStation>();
        public List<string> SourceCorridorIds = new List<string>();
    }

    internal sealed class PlannerRiskCluster
    {
        public string ClusterId = string.Empty;
        public string LocalLineId = string.Empty;
        public string ExpressLineId = string.Empty;
        public string PairRole = string.Empty;
        public string ResolutionState = string.Empty;
        public bool IsPrimaryPlanningRisk = false;
        public string YieldingLineId = string.Empty;
        public string PriorityLineId = string.Empty;
        public string FromStationId = string.Empty;
        public string ToStationId = string.Empty;
        public int CatchupCount = 0;
        public float MaxSeverityMinutes = 0f;
        public float UnresolvedRiskMinutes = 0f;
        public float RobustnessRiskMinutes = 0f;
        public float TotalExpressSavedMinutes = 0f;
        public float TotalLocalWaitMinutes = 0f;
        public List<string> RecommendedActionCodes = new List<string>();
        public List<string> CatchupIds = new List<string>();
        public List<string> SourceCorridorIds = new List<string>();
        public PlannerBypassEvaluation RecommendedBypassStation;
    }

    internal sealed class PlannerOptimizationRegion
    {
        public string RegionId = string.Empty;
        public List<string> ClusterIds = new List<string>();
        public List<string> YieldingLineIds = new List<string>();
        public List<string> PriorityLineIds = new List<string>();
        public int EventCount = 0;
        public float FirstCatchupMinute = 0f;
        public float LastCatchupMinute = 0f;
        public float TotalUnresolvedRiskMinutes = 0f;
        public float TotalRobustnessRiskMinutes = 0f;
    }

    internal sealed class PlannerPlanModel
    {
        public string PlanId = string.Empty;
        public string ObjectiveId = string.Empty;
        public string Status = "notComputed";
        public float Score = 0f;
        public float ExpressSavedMinutes = 0f;
        public float LocalWaitMinutes = 0f;
        public float UnresolvedRiskMinutes = 0f;
        public float RobustnessRiskMinutes = 0f;
        public int AddedBypassStationCount = 0;
        public int RetimedTripCount = 0;
        public int RecommendedExpressOffsetDeltaMinutes = 0;
        public List<PlannerValidationIssue> Diagnostics = new List<PlannerValidationIssue>();
        public List<PlannerRiskCluster> RiskClusters = new List<PlannerRiskCluster>();
        public List<PlannerCatchupEvent> CatchupEvents = new List<PlannerCatchupEvent>();
        public List<string> SelectedBypassStationIds = new List<string>();
        public List<DispatchPlannerScheduleActionDto> StructuredScheduleActions = new List<DispatchPlannerScheduleActionDto>();
        public List<DispatchPlannerProblemIssueDto> ProblemIssues = new List<DispatchPlannerProblemIssueDto>();
        public DispatchPlannerFrontendSummaryDto FrontendSummary;
        public PlannerCapacityDiagnostic CapacityDiagnostic;
        public List<PlannerWorkingRow> BaselineRows = new List<PlannerWorkingRow>();
        public List<PlannerWorkingRow> AdjustedRows = new List<PlannerWorkingRow>();
        public List<DispatchPlannerPreviewRowDto> PreviewRows = new List<DispatchPlannerPreviewRowDto>();
    }

    internal sealed class PlannerCapacityDiagnostic
    {
        public bool Success = false;
        public string OverallVerdict = "insufficientData";
        public bool CapacityLikely = false;
        public float MinGapMinutes = 0f;
        public float HighestCapacityConsumptionRatio = 0f;
        public float HighestCapacityConsumptionPercent = 0f;
        public float HighestCompressedSpanMinutes = 0f;
        public float HighestZeroGapConsumptionRatio = 0f;
        public float RequiredMaxShiftMinutes = 0f;
        public float RequiredMaxWaitMinutes = 0f;
        public float MinResidualSlackMinutes = 0f;
        public string CriticalResourceId = string.Empty;
        public string CriticalTargetLineId = string.Empty;
        public string[] CriticalCoverageLineIds = new string[0];
        public string[] CriticalCoverageLines = new string[0];
        public int CriticalTargetStartAtomIndex = -1;
        public int CriticalTargetEndAtomIndexExclusive = -1;
        public int TripCount = 0;
        public int ExportedSharedCorridorCount = 0;
        public int ValidSharedCorridorCount = 0;
        public int RelevantSharedCorridorCount = 0;
        public int ProjectedIntervalCount = 0;
        public int ElementarySectionCount = 0;
        public int ReportGroupCount = 0;
        public string Reason = string.Empty;
        public string Summary = string.Empty;
    }

    internal sealed class PlannerContext
    {
        public DispatchPlannerExportSnapshot Snapshot = new DispatchPlannerExportSnapshot();
        public DispatchPlannerRequest Request = new DispatchPlannerRequest();
        public DispatchPlannerDraftDto SelectedDraft;
        public string[] SelectedLineIds = new string[0];
        public string[] EffectiveLineIds = new string[0];
        public string[] AutoFixedConstraintLineIds = new string[0];
        public int SuppressedFixedVsFixedClusterCount = 0;
        public string[] AdjustableLineIds = new string[0];
        public string[] FixedLineIds = new string[0];
        public string[] TargetLineIds = new string[0];
        public string[] ActiveVirtualBypassStationIds = new string[0];
        public int ActiveExpressOffsetMinutes = 0;
        public string[] SelectedLocalLineIds = new string[0];
        public string[] SelectedExpressLineIds = new string[0];
        public string VirtualExpressLineId = string.Empty;
        public string[] SelectedExpressStopStationIds = new string[0];
        public string[] ForcedBypassStationIds = new string[0];
        public string WindowStart = "00:00";
        public string WindowEnd = "23:59";
        public int WindowStartMinute = 0;
        public int WindowEndMinute = 1439;
        public string ExpressSourceMode = "virtual";
        public string DepartureMode = "fixedInterval";
        public string VirtualExpressBaseLineId = string.Empty;
        public Dictionary<string, DispatchPlannerLineDto> LinesById = new Dictionary<string, DispatchPlannerLineDto>(StringComparer.Ordinal);
        public Dictionary<string, DispatchPlannerStationDto> StationsById = new Dictionary<string, DispatchPlannerStationDto>(StringComparer.Ordinal);
        public Dictionary<string, List<DispatchPlannerStationDto>> StationsByLineId = new Dictionary<string, List<DispatchPlannerStationDto>>(StringComparer.Ordinal);
        public Dictionary<string, List<DispatchPlannerSegmentDto>> SegmentsByLineId = new Dictionary<string, List<DispatchPlannerSegmentDto>>(StringComparer.Ordinal);
        public Dictionary<string, DispatchPlannerLineTrackDto> LineTracksByLineId = new Dictionary<string, DispatchPlannerLineTrackDto>(StringComparer.Ordinal);
        public Dictionary<string, DispatchPlannerStationDwellObservationDto> StopDwellByStationId = new Dictionary<string, DispatchPlannerStationDwellObservationDto>(StringComparer.Ordinal);
        public Dictionary<string, PlannerObservedRuntimeSummary> StationRuntimeByLinePair = new Dictionary<string, PlannerObservedRuntimeSummary>(StringComparer.Ordinal);
        public Dictionary<string, List<PlannerBypassStation>> ConfiguredBypassStationsByLineId = new Dictionary<string, List<PlannerBypassStation>>(StringComparer.Ordinal);
        public Dictionary<string, List<PlannerBypassStation>> CandidateBypassStationsByLineId = new Dictionary<string, List<PlannerBypassStation>>(StringComparer.Ordinal);
        public List<PlannerWorkingRow> WorkingRows = new List<PlannerWorkingRow>();
        public List<PlannerValidationIssue> ValidationIssues = new List<PlannerValidationIssue>();
    }

    internal sealed class PlannerExecutionState
    {
        public PlannerContext Context = new PlannerContext();
        public PlannerRuntimeCatalog RuntimeCatalog = new PlannerRuntimeCatalog();
        public List<PursuitTrunk> PursuitTrunks = new List<PursuitTrunk>();
        public List<PlannerTripModel> Trips = new List<PlannerTripModel>();
        public List<PlannerCatchupEvent> CatchupEvents = new List<PlannerCatchupEvent>();
        public List<PlannerRiskCluster> RiskClusters = new List<PlannerRiskCluster>();
        public List<PlannerOptimizationRegion> OptimizationRegions = new List<PlannerOptimizationRegion>();
        public List<PlannerPlanModel> Plans = new List<PlannerPlanModel>();
        public PlannerCapacityDiagnostic BaselineCapacityDiagnostic;
        public List<PlannerValidationIssue> Diagnostics = new List<PlannerValidationIssue>();
    }

    internal static class PlannerDiagnosticFactory
    {
        public static PlannerValidationIssue Create(string level, string code, string message)
        {
            PlannerValidationIssue issue = new PlannerValidationIssue();
            issue.Level = level ?? "info";
            issue.Code = code ?? string.Empty;
            issue.Message = message ?? string.Empty;
            issue.RelatedClusterIds = new string[0];
            return issue;
        }
    }
}
