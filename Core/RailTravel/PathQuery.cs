using System.Collections.Generic;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.RailTravel
{
    internal sealed class PathQuery
    {
        private readonly EntityManager m_EntityManager;

        public PathQuery(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        public bool TryBuild(Entity pathOwner, out Path path)
        {
            return TryBuild(pathOwner, false, out path);
        }

        internal bool TryBuildTheoryDepot(Entity pathOwner, out Path path)
        {
            return TryBuild(pathOwner, true, out path);
        }

        private bool TryBuild(Entity pathOwner, bool theoryDepot, out Path path)
        {
            path = null;
            if (pathOwner == Entity.Null
                || !m_EntityManager.Exists(pathOwner)
                || !m_EntityManager.HasBuffer<PathElement>(pathOwner))
            {
                return false;
            }

            DynamicBuffer<PathElement> elements = m_EntityManager.GetBuffer<PathElement>(pathOwner, true);
            if (elements.Length == 0)
                return false;

            int firstCurve = elements.Length;
            int lastCurve = -1;
            for (int i = 0; i < elements.Length; i++)
            {
                if (!m_EntityManager.HasComponent<Curve>(elements[i].m_Target))
                    continue;
                firstCurve = System.Math.Min(firstCurve, i);
                lastCurve = i;
            }

            var segments = new List<Segment>(elements.Length);
            int skipped = 0;
            int nonNoiseSkipped = 0;
            for (int i = 0; i < elements.Length; i++)
            {
                PathElement element = elements[i];
                bool hasConnection = m_EntityManager.HasComponent<ConnectionLane>(element.m_Target);
                TrackTypes connectionTrackTypes = hasConnection ? m_EntityManager.GetComponentData<ConnectionLane>(element.m_Target).m_TrackTypes : TrackTypes.None;
                TrackAtomClass atomClass = TrackBuild.ClassifyPathElementTarget(
                    element.m_Flags,
                    m_EntityManager.HasComponent<TrackLane>(element.m_Target),
                    hasConnection,
                    connectionTrackTypes,
                    m_EntityManager.HasComponent<EdgeLane>(element.m_Target));
                if (atomClass == TrackAtomClass.FilteredNoise)
                {
                    skipped++;
                    continue;
                }
                if (!m_EntityManager.HasComponent<Curve>(element.m_Target))
                {
                    if (theoryDepot && IsTheoryDepotEndpoint(element.m_Target, i, firstCurve, lastCurve, pathOwner))
                    {
                        skipped++;
                        continue;
                    }
                    nonNoiseSkipped++;
                    continue;
                }

                Curve curve = m_EntityManager.GetComponentData<Curve>(element.m_Target);
                if (atomClass == TrackAtomClass.PrimaryLane && m_EntityManager.HasComponent<TrackLane>(element.m_Target))
                {
                    TrackLane trackLane = m_EntityManager.GetComponentData<TrackLane>(element.m_Target);
                    segments.Add(new Segment(
                        element.m_Target,
                        SegmentKind.TrackLane,
                        element.m_TargetDelta,
                        curve.m_Length,
                        element.m_Flags,
                        trackLane.m_Flags,
                        0,
                        trackLane.m_SpeedLimit,
                        trackLane.m_Curviness));
                    continue;
                }

                if (atomClass == TrackAtomClass.ConnectionHelper)
                {
                    // EdgeLane and flag-marked path connectors share ConnectionHelper physics with ConnectionLane:
                    // IsConnectionLane=true via SegmentKind.ConnectionLane, speed=Calculator.ConnectionSpeed, no TrackLane flags.
                    ConnectionLaneFlags connectionFlags = hasConnection ? m_EntityManager.GetComponentData<ConnectionLane>(element.m_Target).m_Flags : 0;
                    segments.Add(new Segment(
                        element.m_Target,
                        SegmentKind.ConnectionLane,
                        element.m_TargetDelta,
                        curve.m_Length,
                        element.m_Flags,
                        0,
                        connectionFlags,
                        Calculator.ConnectionSpeed,
                        0f));
                    continue;
                }

                // Non-noise PathElement that cannot be projected (no Curve / unknown class) must fail the whole path.
                nonNoiseSkipped++;
            }

            // This stays as a narrow projection helper: it reads an existing PathElement buffer
            // and does not own pathfinder timing, request lifecycles, or depot-specific setup.
            if (segments.Count == 0 || nonNoiseSkipped > 0)
                return false;

            path = new Path(pathOwner, segments.ToArray(), elements.Length, skipped);
            return true;
        }

        private bool IsTheoryDepotEndpoint(Entity target, int index, int firstCurve, int lastCurve, Entity pathOwner)
        {
            if (index >= firstCurve && index <= lastCurve)
                return false;
            if (m_EntityManager.HasComponent<SpawnLocation>(target)
                || m_EntityManager.HasComponent<AccessLane>(target)
                || m_EntityManager.HasComponent<RouteLane>(target))
            {
                return true;
            }
            if (!m_EntityManager.HasComponent<PathInformation>(pathOwner))
                return false;
            PathInformation info = m_EntityManager.GetComponentData<PathInformation>(pathOwner);
            return target == info.m_Origin || target == info.m_Destination;
        }
    }
}
