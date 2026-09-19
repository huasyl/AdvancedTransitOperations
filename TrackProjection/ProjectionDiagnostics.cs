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
        PathReadFailed = 4,
        PathFlagsDisabled = 5,
        PathTruncated = 6,
        PathPendingTail = 7,
    }

    internal struct CurrentLaneMatchDiagnostic
    {
        internal int InitialCandidates;
        internal int IndexedCandidates;
        internal int PhysicalCandidates;
        internal int CoordinateCandidates;
        internal bool InvalidInput;
        internal int FutureCandidates;
        internal ProjectionMatchBasis Basis;
    }

    internal struct ProjectionOutcome
    {
        internal ProjectionRequestSource RequestSource;
        internal int NavigationCandidates;
        internal int PathCandidates;
        internal ProjectionMatchBasis ExactBasis;
        internal ProjectionExactFailure ExactFailure;
        internal bool FinalSuccess;
        internal VehicleTrackCursorSource FinalSource;
        internal long MatcherTicks;
        internal ProjectionRuntimeContext RuntimeContext;
        internal bool ExactCounted;
    }
}
