using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal enum TrackModelRelativeToProtectedInterval : byte
    {
        Unknown = 0,
        Before = 1,
        Inside = 2,
        After = 3,
    }

    internal readonly struct TrackModelRuntimePosition
    {
        public readonly int CurrentControlEdgeIndex;
        public readonly int CurrentAtomIndex;
        public readonly float AtomPosition01;
        public readonly TrackModelRelativeToProtectedInterval RelativeToProtectedInterval;
        public readonly float Confidence;
        public readonly int TraversalPhaseIndex;
        public readonly int TraversalPhaseStartAtomIndex;
        public readonly int TraversalPhaseEndAtomExclusive;
        public readonly int NextTurnbackBoundaryAtomIndex;

        public TrackModelRuntimePosition(
            int currentControlEdgeIndex,
            int currentAtomIndex,
            float atomPosition01,
            TrackModelRelativeToProtectedInterval relativeToProtectedInterval,
            float confidence,
            int traversalPhaseIndex,
            int traversalPhaseStartAtomIndex,
            int traversalPhaseEndAtomExclusive,
            int nextTurnbackBoundaryAtomIndex)
        {
            CurrentControlEdgeIndex = currentControlEdgeIndex;
            CurrentAtomIndex = currentAtomIndex;
            AtomPosition01 = atomPosition01;
            RelativeToProtectedInterval = relativeToProtectedInterval;
            Confidence = confidence;
            TraversalPhaseIndex = traversalPhaseIndex;
            TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
            TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
            NextTurnbackBoundaryAtomIndex = nextTurnbackBoundaryAtomIndex;
        }
    }

    internal enum VehicleTrackCursorSource : byte
    {
        Unknown = 0,
        CurrentLane = 1,
        RouteProgress = 2,
        CachedWaypoint = 3,
        AnchoredRouteProgress = 4,
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
        public readonly VehicleTrackCursor Cursor;

        public VehicleTrackCursorFrameSnapshot(
            Entity lineEntity,
            ulong chainSignature,
            uint frame,
            bool available,
            VehicleTrackCursor cursor)
        {
            LineEntity = lineEntity;
            ChainSignature = chainSignature;
            Frame = frame;
            Available = available;
            Cursor = cursor;
        }
    }

    internal readonly struct LineRunningVehicleSnapshot
    {
        public readonly Entity Vehicle;
        public readonly bool Boarding;
        public readonly bool HasProjection;
        public readonly float ProjectionDistanceMeters;
        public readonly bool HasTrackCursor;
        public readonly VehicleTrackCursor TrackCursor;
        public readonly int CurrentControlEdgeIndex;
        public readonly float OwnLineAtomCoordinate;
        public readonly int PhaseEndAtomExclusive;
        public readonly int TraversalPhaseIndex;
        public readonly int TraversalPhaseStartAtomIndex;
        public readonly int TraversalPhaseEndAtomExclusive;
        public readonly int NextTurnbackBoundaryAtomIndex;

        public LineRunningVehicleSnapshot(
            Entity vehicle,
            bool boarding,
            bool hasProjection,
            float projectionDistanceMeters,
            bool hasTrackCursor,
            VehicleTrackCursor trackCursor,
            int currentControlEdgeIndex,
            float ownLineAtomCoordinate,
            int phaseEndAtomExclusive,
            int traversalPhaseIndex,
            int traversalPhaseStartAtomIndex,
            int traversalPhaseEndAtomExclusive,
            int nextTurnbackBoundaryAtomIndex)
        {
            Vehicle = vehicle;
            Boarding = boarding;
            HasProjection = hasProjection;
            ProjectionDistanceMeters = projectionDistanceMeters;
            HasTrackCursor = hasTrackCursor;
            TrackCursor = trackCursor;
            CurrentControlEdgeIndex = currentControlEdgeIndex;
            OwnLineAtomCoordinate = ownLineAtomCoordinate;
            PhaseEndAtomExclusive = phaseEndAtomExclusive;
            TraversalPhaseIndex = traversalPhaseIndex;
            TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
            TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
            NextTurnbackBoundaryAtomIndex = nextTurnbackBoundaryAtomIndex;
        }
    }

    internal sealed class LineRunningVehicleFrameSnapshot
    {
        public uint Frame;
        public Entity Line;
        public readonly System.Collections.Generic.List<LineRunningVehicleSnapshot> Vehicles = new System.Collections.Generic.List<LineRunningVehicleSnapshot>();
    }
}
