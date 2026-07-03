using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackModelBuilder
    {
        private uint m_Version = 1;
        private bool m_Dirty = true;

        public Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> Track { get; } = new Dictionary<TrackAtomKey, List<SharedTrackOccurrence>>();
        public Dictionary<Entity, List<SharedPhysicalOccurrence>> Physical { get; } = new Dictionary<Entity, List<SharedPhysicalOccurrence>>();

        public void Clear()
        {
            Track.Clear();
            Physical.Clear();
            m_Dirty = true;
        }

        public bool Dirty()
        {
            return m_Dirty;
        }

        public void MarkDirty()
        {
            m_Dirty = true;
        }

        public void ClearDirty()
        {
            m_Dirty = false;
        }

        public uint Version()
        {
            return m_Version;
        }

        public void Bump()
        {
            m_Version++;
        }

        public bool TryTrack(TrackAtomKey key, out List<SharedTrackOccurrence> occurrences)
        {
            return Track.TryGetValue(key, out occurrences);
        }

        public bool TryPhysical(Entity physicalLane, out List<SharedPhysicalOccurrence> occurrences)
        {
            return Physical.TryGetValue(physicalLane, out occurrences);
        }
    }
}
