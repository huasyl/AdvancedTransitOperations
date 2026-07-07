using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
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

            var segments = new List<Segment>(elements.Length);
            int skipped = 0;
            for (int i = 0; i < elements.Length; i++)
            {
                PathElement element = elements[i];
                if (!m_EntityManager.HasComponent<Curve>(element.m_Target))
                {
                    skipped++;
                    continue;
                }

                Curve curve = m_EntityManager.GetComponentData<Curve>(element.m_Target);
                if (m_EntityManager.HasComponent<TrackLane>(element.m_Target))
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

                if (m_EntityManager.HasComponent<ConnectionLane>(element.m_Target))
                {
                    ConnectionLane connectionLane = m_EntityManager.GetComponentData<ConnectionLane>(element.m_Target);
                    segments.Add(new Segment(
                        element.m_Target,
                        SegmentKind.ConnectionLane,
                        element.m_TargetDelta,
                        curve.m_Length,
                        element.m_Flags,
                        0,
                        connectionLane.m_Flags,
                        Calculator.ConnectionSpeed,
                        0f));
                    continue;
                }

                skipped++;
            }

            // This stays as a narrow projection helper: it reads an existing PathElement buffer
            // and does not own pathfinder timing, request lifecycles, or depot-specific setup.
            if (segments.Count == 0)
                return false;

            path = new Path(pathOwner, segments.ToArray(), elements.Length, skipped);
            return true;
        }
    }
}
