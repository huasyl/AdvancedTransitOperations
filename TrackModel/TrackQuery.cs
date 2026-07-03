using System.Collections.Generic;
using Game.Net;
using Game.Routes;
using RapidTransitMod.Bypass;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackQuery
    {
        private readonly TrackState m_State;
        private readonly SharedIndex m_Shared;
        private readonly TrackSupport m_Support;

        internal TrackQuery(TrackState state, SharedIndex shared, TrackSupport support)
        {
            m_State = state;
            m_Shared = shared;
            m_Support = support;
        }

        internal bool TryChain(Entity line, out LineTrackChain chain)
        {
            return m_State.TryChain(line, out chain);
        }

        internal bool TryProfile(Entity line, out LineTraversalProfile profile)
        {
            profile = null;
            if (!m_State.TryChain(line, out LineTrackChain chain) || chain?.TraversalProfile == null)
                return false;

            profile = chain.TraversalProfile;
            return true;
        }

        internal bool TryInterval(Entity line, int intervalIndex, out BypassProtectedInterval interval)
        {
            interval = default;
            if (!m_State.TryChain(line, out LineTrackChain chain)
                || chain == null
                || intervalIndex < 0
                || intervalIndex >= chain.BypassProtectedIntervals.Count)
            {
                return false;
            }

            interval = chain.BypassProtectedIntervals[intervalIndex];
            return true;
        }

        internal bool TryScene(Entity line, int waypointIndex, out LocalBypassWaypointSceneBinding scene)
        {
            scene = default;
            if (!m_State.TryChain(line, out LineTrackChain chain)
                || chain?.LocalBypassWaypointScenes == null
                || waypointIndex < 0
                || waypointIndex >= chain.LocalBypassWaypointScenes.Length)
            {
                return false;
            }

            scene = chain.LocalBypassWaypointScenes[waypointIndex];
            return scene.Available;
        }

        internal bool TryTrack(TrackAtomKey key, out List<SharedTrackOccurrence> occurrences)
        {
            return m_Shared.TryTrack(key, out occurrences);
        }

        internal bool TryPhysical(Entity physicalLane, out List<SharedPhysicalOccurrence> occurrences)
        {
            return m_Shared.TryPhysical(physicalLane, out occurrences);
        }

        internal bool TryGetWaypointIndexLookup(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup)
        {
            lookup = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            ulong signature = ComputeLineWaypointSignature(waypoints);
            if (!m_State.TryWaypointLookup(line, out lookup)
                || lookup == null
                || lookup.Signature != signature)
            {
                lookup = new LineWaypointIndexLookup
                {
                    Signature = signature
                };

                EntityManager entityManager = m_Support.EntityManager;
                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                    if (waypoint == Entity.Null || !entityManager.Exists(waypoint))
                        continue;

                    lookup.WaypointIndexByWaypoint[waypoint] = waypointIndex;
                    if (entityManager.HasComponent<Connected>(waypoint))
                    {
                        Entity stop = entityManager.GetComponentData<Connected>(waypoint).m_Connected;
                        if (stop != Entity.Null)
                            lookup.WaypointIndexByStop[stop] = waypointIndex;
                    }
                }

                m_State.PutWaypointLookup(line, lookup);
            }

            return true;
        }

        private static ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> wps)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, wps.Length);
            for (int i = 0; i < wps.Length; i++)
                hash = MixLineSignature(hash, wps[i].m_Waypoint.Index);
            return hash;
        }

        private static ulong MixLineSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }
    }
}
