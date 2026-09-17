using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackProjection
{
    internal enum ProjectionReadKind : byte
    {
        CurrentLane = 1,
        Navigation = 2,
        Path = 3,
    }

    internal enum ProjectionRequestSource : byte
    {
        Unknown = 0,
        Bypass = 1,
        Announcement = 2,
        Observation = 3,
        Waypoint = 4,
        Signal = 5,
        StationContext = 6,
    }

    internal struct ProjectionRuntimeContext
    {
        internal bool VehicleStateKnown;
        internal VehicleState VehicleState;
        internal bool PublicTransportKnown;
        internal bool OfficialBoarding;
        internal bool StopSessionKnown;
        internal Entity StopSessionLine;
        internal int StopSessionWaypoint;
        internal uint StopSessionArrivalFrame;
    }

    internal enum ProjectionFallbackFailure : byte
    {
        None = 0,
        NoRouteProgressOrCache = 1,
        SegmentUnavailable = 2,
        EmptySegment = 3,
        SuspectProgress = 4,
        LinePending = 5,
        ChainUnavailable = 6,
        NotRequested = 7,
    }

    internal enum ProjectionMatchBasis : byte
    {
        None = 0,
        Initial = 1,
        Direction = 2,
        Navigation = 3,
        PathTail = 4,
        IndependentBoarding = 5,
        DepartureSession = 6,
        ArrivalTarget = 7,
    }

    internal enum ProjectionExactFailure : byte
    {
        None = 0,
        CurrentLaneUnavailable = 1,
        NoCandidates = 2,
        AllExcluded = 3,
        Ambiguous = 4,
        ZeroParameterSpan = 5,
        ProgressUnavailable = 6,
        LaneNotInModel = 7,
        ParameterOutsideRange = 8,
        InvalidParameter = 9,
        IndexedLaneMismatch = 10,
    }

    internal enum ProjectionReadStop : byte
    {
        None = 0,
        PathWrite = 1,
        NavigationReadFailed = 2,
        NavigationTruncated = 3,
        PathReadFailed = 4,
        PathFlagsDisabled = 5,
        PathTruncated = 6,
        PathPendingTail = 7,
    }

    internal enum ProjectionEvidenceStage : byte
    {
        None = 0,
        Direction = 1,
        Navigation = 2,
        PathTail = 3,
    }

    internal enum ProjectionEvidenceKind : byte
    {
        None = 0,
        Mismatch = 1,
        NoEvidence = 2,
        DirectionExcluded = 3,
    }

    internal enum ProjectionEvidenceReason : byte
    {
        None = 0,
        OppositeDirection = 1,
        EmptyInput = 2,
        FilteredInput = 3,
        UnknownLaneStop = 4,
        CurrentTailUnavailable = 5,
        EndOfPathOrReturn = 6,
        InvalidParameters = 7,
        LaneMismatch = 8,
        DirectionMismatch = 9,
        ParameterStartMismatch = 10,
        ParameterRangeOverrun = 12,
        ModelExhausted = 13,
    }

    internal struct ProjectionMatchEvidence
    {
        internal ProjectionEvidenceKind Kind;
        internal ProjectionEvidenceReason Reason;
        internal ProjectionEvidenceStage Stage;
        internal int CandidateAtomIndex;
        internal int InitialAtomIndex;
        internal int QueueIndex;
        internal Entity ExpectedLane;
        internal float2 ExpectedParameters;
        internal Entity ActualLane;
        internal float4 ActualParameters;
        internal TrainLaneFlags ActualFlags;

        internal bool Available => Kind != ProjectionEvidenceKind.None;

        internal bool ShouldReplaceWith(ProjectionMatchEvidence candidate)
        {
            if (!candidate.Available)
                return false;
            if (!Available)
                return true;
            if (candidate.Kind == ProjectionEvidenceKind.Mismatch)
                return Kind != ProjectionEvidenceKind.Mismatch || candidate.Stage >= Stage;
            if (Kind == ProjectionEvidenceKind.Mismatch)
                return false;
            if (Kind == ProjectionEvidenceKind.DirectionExcluded)
                return candidate.Kind != ProjectionEvidenceKind.DirectionExcluded;
            if (candidate.Kind == ProjectionEvidenceKind.DirectionExcluded)
                return true;

            bool candidateStops = candidate.Reason == ProjectionEvidenceReason.UnknownLaneStop
                || candidate.Reason == ProjectionEvidenceReason.EndOfPathOrReturn;
            bool currentStops = Reason == ProjectionEvidenceReason.UnknownLaneStop
                || Reason == ProjectionEvidenceReason.EndOfPathOrReturn;
            return candidateStops && !currentStops;
        }
    }

    internal struct ProjectionMismatchSamples
    {
        internal ProjectionMatchEvidence First;
        internal ProjectionMatchEvidence Second;

        internal void Add(ProjectionMatchEvidence evidence)
        {
            if (evidence.Kind != ProjectionEvidenceKind.Mismatch)
                return;
            if (!First.Available)
            {
                First = evidence;
                return;
            }
            if (First.InitialAtomIndex != evidence.InitialAtomIndex && !Second.Available)
                Second = evidence;
        }
    }

    internal struct CurrentLaneMatchDiagnostic
    {
        internal int InitialCandidates;
        internal int IndexedCandidates;
        internal int PhysicalCandidates;
        internal int CoordinateCandidates;
        internal bool InvalidInput;
        internal int LastCandidateAtomIndex;
        internal float2 LastCandidateRange;
        internal int DirectionCandidates;
        internal int FutureCandidates;
        internal Entity OverlapLane;
        internal ProjectionMatchBasis Basis;
        internal ProjectionMatchEvidence Evidence;
        internal ProjectionMismatchSamples Mismatches;
        internal bool ExtensionReturnMatched;
        internal int ExtensionStartAtomIndex;
        internal int ExtensionForwardEndAtomIndexExclusive;
        internal int ExtensionResumeAtomIndex;
        internal Entity ExtensionReturnLane;
        internal float ExtensionReturnPosition;
        internal int ExtensionCandidateAtomIndex;
        internal ProjectionEvidenceStage ExtensionStage;
        internal int ExtensionQueueIndex;
    }

    internal struct ProjectionOutcome
    {
        internal uint Frame;
        internal ProjectionRequestSource RequestSource;
        internal Entity Vehicle;
        internal Entity Line;
        internal ulong ChainSignature;
        internal Entity FrontLane;
        internal float4 FrontCurvePosition;
        internal TrainLaneFlags FrontFlags;
        internal int InitialCandidates;
        internal int IndexedCandidates;
        internal int PhysicalCandidates;
        internal int CoordinateCandidates;
        internal bool MatcherInputInvalid;
        internal int LastCandidateAtomIndex;
        internal float2 LastCandidateRange;
        internal int DirectionCandidates;
        internal int NavigationCandidates;
        internal int PathCandidates;
        internal Entity OverlapLane;
        internal ProjectionMatchBasis ExactBasis;
        internal int IndependentBoardingWaypoint;
        internal int DepartureSessionWaypoint;
        internal int ArrivalTargetWaypoint;
        internal ProjectionExactFailure ExactFailure;
        internal ProjectionReadStop ReadStop;
        internal ProjectionMatchEvidence Evidence;
        internal ProjectionMismatchSamples Mismatches;
        internal bool ExtensionReturnMatched;
        internal int ExtensionStartAtomIndex;
        internal int ExtensionForwardEndAtomIndexExclusive;
        internal int ExtensionResumeAtomIndex;
        internal Entity ExtensionReturnLane;
        internal float ExtensionReturnPosition;
        internal int ExtensionCandidateAtomIndex;
        internal ProjectionEvidenceStage ExtensionStage;
        internal int ExtensionQueueIndex;
        internal bool FinalSuccess;
        internal int FinalAtomIndex;
        internal float FinalAtomPosition01;
        internal VehicleTrackCursorSource FinalSource;
        internal long MatcherTicks;
        internal bool RouteProgressKnown;
        internal int RouteProgressWaypoint;
        internal float RouteProgressProportion;
        internal bool CachedWaypointUsed;
        internal int CachedWaypoint;
        internal bool StationAnchorUsed;
        internal int StationAnchorWaypoint;
        internal int HistoryAtomBefore;
        internal int HistoryAtomAfter;
        internal ProjectionFallbackFailure FallbackFailure;
        internal ProjectionRuntimeContext RuntimeContext;
        internal bool ExactCounted;
    }
}
