using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal enum VehicleTrackCursorSource : byte
    {
        Unknown = 0,
        CurrentLane = 1,
        RouteProgress = 2,
        CachedWaypoint = 3,
        AnchoredRouteProgress = 4,
    }

    internal enum CurrentLanePositionFailure : byte
    {
        None = 0,
        LinePending = 1,
        CurrentFrameCursorUnavailable = 2,
        CursorSourceNotCurrentLane = 3,
        CursorLineOrSignatureMismatch = 4,
        CursorIndexInconsistent = 5,
        TrainCurrentLaneUnavailable = 6,
        CurrentLaneUnavailable = 7,
        CurvePositionNotFinite = 8,
        CurrentLaneNotIndexed = 9,
        CurrentLaneAtomNotFound = 10,
        CurrentLaneAtomSpanInvalid = 11,
        CurrentLaneProgressNotFinite = 12,
    }

    internal readonly struct CurrentLanePositionDiagnostic
    {
        internal readonly CurrentLanePositionFailure Failure;
        internal readonly Entity CurrentLane;
        internal readonly float CurveY;
        internal readonly VehicleTrackCursorSource CursorSource;
        internal readonly int CursorSegmentIndex;
        internal readonly int CursorAtomIndex;

        internal CurrentLanePositionDiagnostic(
            CurrentLanePositionFailure failure,
            Entity currentLane,
            float curveY,
            VehicleTrackCursorSource cursorSource,
            int cursorSegmentIndex,
            int cursorAtomIndex)
        {
            Failure = failure;
            CurrentLane = currentLane;
            CurveY = curveY;
            CursorSource = cursorSource;
            CursorSegmentIndex = cursorSegmentIndex;
            CursorAtomIndex = cursorAtomIndex;
        }
    }

    internal readonly struct VehicleTrackCursor
    {
        public readonly bool Available;
        public readonly Entity LineEntity;
        public readonly ulong ChainSignature;
        public readonly int SegmentIndex;
        public readonly int AtomStartIndex;
        public readonly int AtomEndIndexExclusive;
        public readonly int AtomCursorIndex;
        public readonly float AtomPosition01;
        public readonly float Confidence;
        public readonly VehicleTrackCursorSource Source;

        public VehicleTrackCursor(
            Entity lineEntity,
            ulong chainSignature,
            int segmentIndex,
            int atomStartIndex,
            int atomEndIndexExclusive,
            int atomCursorIndex,
            float atomPosition01,
            float confidence,
            VehicleTrackCursorSource source = VehicleTrackCursorSource.Unknown)
        {
            Available = lineEntity != Entity.Null;
            LineEntity = lineEntity;
            ChainSignature = chainSignature;
            SegmentIndex = segmentIndex;
            AtomStartIndex = atomStartIndex;
            AtomEndIndexExclusive = atomEndIndexExclusive;
            AtomCursorIndex = atomCursorIndex;
            AtomPosition01 = atomPosition01;
            Confidence = confidence;
            Source = source;
        }
    }

    internal readonly struct VehicleTrackCursorFrameSnapshot
    {
        public readonly Entity LineEntity;
        public readonly ulong ChainSignature;
        public readonly uint Frame;
        public readonly bool Available;
        public readonly bool FinalKnown;
        public readonly VehicleTrackCursor Cursor;
        public readonly bool ExactKnown;
        public readonly bool ExactAvailable;
        public readonly VehicleTrackCursor ExactCursor;

        public VehicleTrackCursorFrameSnapshot(
            Entity lineEntity,
            ulong chainSignature,
            uint frame,
            bool available,
            VehicleTrackCursor cursor,
            bool finalKnown = false,
            bool exactKnown = false,
            bool exactAvailable = false,
            VehicleTrackCursor exactCursor = default)
        {
            LineEntity = lineEntity;
            ChainSignature = chainSignature;
            Frame = frame;
            Available = available;
            FinalKnown = finalKnown;
            Cursor = cursor;
            ExactKnown = exactKnown;
            ExactAvailable = exactAvailable;
            ExactCursor = exactCursor;
        }
    }

}
