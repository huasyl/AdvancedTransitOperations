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

        public VehicleTrackFacts(
            uint frame,
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            VehicleTrackCursor cursor)
        {
            Frame = frame;
            Vehicle = vehicle;
            Line = line;
            ChainSignature = chainSignature;
            Cursor = cursor;
        }
    }
}
