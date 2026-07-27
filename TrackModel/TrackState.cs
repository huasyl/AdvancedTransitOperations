using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackState
    {
        private readonly TrackModelStore m_Store = new TrackModelStore();
        private readonly Dictionary<Entity, LineTrackChainFrameSnapshot> m_LineTrackChainFrameSnapshots = new Dictionary<Entity, LineTrackChainFrameSnapshot>();
        private readonly Dictionary<Entity, LineWaypointIndexLookup> m_LineWaypointIndexLookups = new Dictionary<Entity, LineWaypointIndexLookup>();

        internal bool TryChain(Entity line, out LineTrackChain chain) => m_Store.Get(line, out chain);
        internal void PutChain(Entity line, LineTrackChain chain) => m_Store.Put(line, chain);
        internal bool IsDirty(Entity line) => m_Store.Dirty(line);
        internal void MarkDirty(Entity line) => m_Store.MarkDirty(line);

        internal bool RemoveLine(Entity line, out LineTrackChain chain)
        {
            m_LineTrackChainFrameSnapshots.Remove(line);
            m_LineWaypointIndexLookups.Remove(line);
            return m_Store.Remove(line, out chain);
        }

        internal void ClearLines()
        {
            m_Store.Clear();
            m_LineTrackChainFrameSnapshots.Clear();
            m_LineWaypointIndexLookups.Clear();
        }

        internal bool TryFrameSnapshot(Entity line, out LineTrackChainFrameSnapshot snapshot)
            => m_LineTrackChainFrameSnapshots.TryGetValue(line, out snapshot);

        internal void PutFrameSnapshot(Entity line, LineTrackChainFrameSnapshot snapshot)
        {
            if (line != Entity.Null)
                m_LineTrackChainFrameSnapshots[line] = snapshot;
        }

        internal bool TryWaypointLookup(Entity line, out LineWaypointIndexLookup lookup)
            => m_LineWaypointIndexLookups.TryGetValue(line, out lookup);

        internal void PutWaypointLookup(Entity line, LineWaypointIndexLookup lookup)
        {
            if (line != Entity.Null && lookup != null)
                m_LineWaypointIndexLookups[line] = lookup;
        }

        internal void RemoveWaypointLookup(Entity line)
        {
            if (line != Entity.Null)
                m_LineWaypointIndexLookups.Remove(line);
        }

        internal void ClearWaypointLookups() => m_LineWaypointIndexLookups.Clear();

        private sealed class TrackModelStore
        {
            private readonly Dictionary<Entity, LineTrackChain> m_Chains = new Dictionary<Entity, LineTrackChain>();
            private readonly HashSet<Entity> m_DirtyLines = new HashSet<Entity>();

            public bool Get(Entity line, out LineTrackChain chain)
            {
                return m_Chains.TryGetValue(line, out chain);
            }

            public void Put(Entity line, LineTrackChain chain)
            {
                if (line == Entity.Null || chain == null)
                    return;

                m_Chains[line] = chain;
                m_DirtyLines.Remove(line);
            }

            public bool Remove(Entity line, out LineTrackChain chain)
            {
                bool found = m_Chains.TryGetValue(line, out chain);
                m_Chains.Remove(line);
                return found;
            }

            public void Clear()
            {
                m_Chains.Clear();
                m_DirtyLines.Clear();
            }

            public bool Dirty(Entity line)
            {
                return line != Entity.Null && m_DirtyLines.Contains(line);
            }

            public void MarkDirty(Entity line)
            {
                if (line != Entity.Null)
                    m_DirtyLines.Add(line);
            }
        }
    }

    internal sealed class LineWaypointIndexLookup
    {
        public ulong Signature;
        public int LastStopWaypointIndex = -1;
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
