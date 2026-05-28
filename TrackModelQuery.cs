using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;
using BypassProtectedInterval = RapidTransitMod.DispatchRuntimeSystem.BypassProtectedInterval;
using LineTrackChain = RapidTransitMod.DispatchRuntimeSystem.LineTrackChain;
using LineTraversalProfile = RapidTransitMod.DispatchRuntimeSystem.LineTraversalProfile;
using LocalBypassWaypointSceneBinding = RapidTransitMod.DispatchRuntimeSystem.LocalBypassWaypointSceneBinding;
using SharedPhysicalOccurrence = RapidTransitMod.DispatchRuntimeSystem.SharedPhysicalOccurrence;
using SharedTrackOccurrence = RapidTransitMod.DispatchRuntimeSystem.SharedTrackOccurrence;
using TrackAtomKey = RapidTransitMod.DispatchRuntimeSystem.TrackAtomKey;

namespace RapidTransitMod
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

    internal sealed class TrackModelQuery
    {
        private readonly TrackModelStore m_Store;
        private readonly TrackModelBuilder m_Builder;

        public TrackModelQuery(TrackModelStore store, TrackModelBuilder builder)
        {
            m_Store = store;
            m_Builder = builder;
        }

        public bool TryChain(Entity line, out LineTrackChain chain)
        {
            return m_Store.Get(line, out chain);
        }

        public bool TryProfile(Entity line, out LineTraversalProfile profile)
        {
            profile = null;
            if (!m_Store.Get(line, out LineTrackChain chain) || chain?.TraversalProfile == null)
                return false;

            profile = chain.TraversalProfile;
            return true;
        }

        public bool TryInterval(Entity line, int intervalIndex, out BypassProtectedInterval interval)
        {
            interval = default;
            if (!m_Store.Get(line, out LineTrackChain chain)
                || chain == null
                || intervalIndex < 0
                || intervalIndex >= chain.BypassProtectedIntervals.Count)
            {
                return false;
            }

            interval = chain.BypassProtectedIntervals[intervalIndex];
            return true;
        }

        public bool TryScene(Entity line, int waypointIndex, out LocalBypassWaypointSceneBinding scene)
        {
            scene = default;
            if (!m_Store.Get(line, out LineTrackChain chain)
                || chain?.LocalBypassWaypointScenes == null
                || waypointIndex < 0
                || waypointIndex >= chain.LocalBypassWaypointScenes.Length)
            {
                return false;
            }

            scene = chain.LocalBypassWaypointScenes[waypointIndex];
            return scene.Available;
        }

        public bool TryTrack(TrackAtomKey key, out List<SharedTrackOccurrence> occurrences)
        {
            return m_Builder.TryTrack(key, out occurrences);
        }

        public bool TryPhysical(Entity physicalLane, out List<SharedPhysicalOccurrence> occurrences)
        {
            return m_Builder.TryPhysical(physicalLane, out occurrences);
        }
    }
}
