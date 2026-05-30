using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackModelStore
    {
        public Dictionary<Entity, LineTrackChain> Chains { get; } = new Dictionary<Entity, LineTrackChain>();
        public HashSet<Entity> DirtyLines { get; } = new HashSet<Entity>();

        public bool Get(Entity line, out LineTrackChain chain)
        {
            return Chains.TryGetValue(line, out chain);
        }

        public void Put(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null || chain == null)
                return;

            Chains[line] = chain;
            DirtyLines.Remove(line);
        }

        public bool Remove(Entity line, out LineTrackChain chain)
        {
            bool found = Chains.TryGetValue(line, out chain);
            Chains.Remove(line);
            return found;
        }

        public void Clear()
        {
            Chains.Clear();
            DirtyLines.Clear();
        }

        public bool Dirty(Entity line)
        {
            return line != Entity.Null && DirtyLines.Contains(line);
        }

        public void MarkDirty(Entity line)
        {
            if (line != Entity.Null)
                DirtyLines.Add(line);
        }
    }
}
