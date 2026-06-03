using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineTimes
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public LineTimes(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public float Duration(Entity line)
        {
            EntityManager entityManager = m_Runtime.EntityManager;
            if (!entityManager.HasComponent<PrefabRef>(line)) return 0f;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!entityManager.HasComponent<TransportLineData>(prefab)) return 0f;
            float stopDuration = entityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration;

            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            BufferLookup<RouteSegment> segBuffers = m_Runtime.GetBufferLookup<RouteSegment>(true);
            ComponentLookup<PathInformation> pathInfoLookup = m_Runtime.GetComponentLookup<PathInformation>(true);
            ComponentLookup<VehicleTiming> vehicleTimingLookup = m_Runtime.GetComponentLookup<VehicleTiming>(true);

            if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints)) return 0f;
            if (!segBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments)) return 0f;
            if (waypoints.Length == 0 || segments.Length == 0) return 0f;

            int firstWaypoint = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (vehicleTimingLookup.HasComponent(waypoints[w].m_Waypoint))
                {
                    firstWaypoint = w;
                    break;
                }
            }

            float duration = 0f;
            for (int i = 0; i < waypoints.Length; i++)
            {
                int wi = (firstWaypoint + i) % waypoints.Length;
                int wi1 = (wi + 1) % waypoints.Length;
                Entity segment = segments[wi].m_Segment;
                if (pathInfoLookup.TryGetComponent(segment, out PathInformation pathInfo))
                    duration += pathInfo.m_Duration;
                if (vehicleTimingLookup.HasComponent(waypoints[wi1].m_Waypoint))
                    duration += stopDuration;
            }

            return duration;
        }
    }
}
