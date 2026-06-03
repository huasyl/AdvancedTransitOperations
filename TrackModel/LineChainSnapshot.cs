using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed partial class TrackModelService
    {
        private readonly Dictionary<Entity, LineTrackChainFrameSnapshot> m_LineTrackChainFrameSnapshots = new Dictionary<Entity, LineTrackChainFrameSnapshot>();
        private readonly Dictionary<Entity, LineWaypointIndexLookup> m_LineWaypointIndexLookups = new Dictionary<Entity, LineWaypointIndexLookup>();
    }

    internal sealed class LineWaypointIndexLookup
    {
        public ulong Signature;
        public readonly Dictionary<Entity, int> WaypointIndexByWaypoint = new Dictionary<Entity, int>();
        public readonly Dictionary<Entity, int> WaypointIndexByStop = new Dictionary<Entity, int>();
    }

    internal readonly struct LineTrackChainFrameSnapshot
    {
        public readonly uint Frame;
        public readonly int WaypointCount;
        public readonly bool Available;
        public readonly LineTrackChain Chain;

        public LineTrackChainFrameSnapshot(uint frame, int waypointCount, bool available, LineTrackChain chain)
        {
            Frame = frame;
            WaypointCount = waypointCount;
            Available = available;
            Chain = chain;
        }
    }
}
