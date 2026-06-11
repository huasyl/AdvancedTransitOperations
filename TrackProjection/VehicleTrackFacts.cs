using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal readonly struct VehicleTrackFacts
    {
        public readonly uint Frame;
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly ulong ChainSignature;
        public readonly VehicleTrackCursor Cursor;
        public readonly int CurrentControlEdgeIndex;
        public readonly float OwnLineAtomCoordinate;
        public readonly int PhaseEndAtomExclusive;
        public readonly int TraversalPhaseIndex;
        public readonly int TraversalPhaseStartAtomIndex;
        public readonly int TraversalPhaseEndAtomExclusive;
        public readonly int NextTurnbackBoundaryAtomIndex;

        public VehicleTrackFacts(
            uint frame,
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            VehicleTrackCursor cursor,
            int currentControlEdgeIndex,
            float ownLineAtomCoordinate,
            int phaseEndAtomExclusive,
            int traversalPhaseIndex,
            int traversalPhaseStartAtomIndex,
            int traversalPhaseEndAtomExclusive,
            int nextTurnbackBoundaryAtomIndex)
        {
            Frame = frame;
            Vehicle = vehicle;
            Line = line;
            ChainSignature = chainSignature;
            Cursor = cursor;
            CurrentControlEdgeIndex = currentControlEdgeIndex;
            OwnLineAtomCoordinate = ownLineAtomCoordinate;
            PhaseEndAtomExclusive = phaseEndAtomExclusive;
            TraversalPhaseIndex = traversalPhaseIndex;
            TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
            TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
            NextTurnbackBoundaryAtomIndex = nextTurnbackBoundaryAtomIndex;
        }
    }
}
