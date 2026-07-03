using System;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal static class DispatchLineEligibility
    {
        public const string ReasonUnsupportedTransportMode = "unsupported-transport-mode";
        public const string ReasonCargoTransport = "cargo-transport";

        public static bool IsDispatchTransportLine(EntityManager entityManager, Entity line)
        {
            return IsDispatchTransportLine(entityManager, line, out _);
        }

        public static bool IsDispatchTransportLine(EntityManager entityManager, Entity line, out string reason)
        {
            reason = string.Empty;
            if (line == Entity.Null
                || !entityManager.Exists(line)
                || !entityManager.HasBuffer<RouteWaypoint>(line)
                || !TryGetTransportLineData(entityManager, line, out TransportLineData lineData))
            {
                reason = ReasonUnsupportedTransportMode;
                return false;
            }

            if (lineData.m_CargoTransport)
            {
                reason = ReasonCargoTransport;
                return false;
            }

            if (!TransportModeProfile.GetProfile(TransportModeResolver.Resolve(lineData)).CanDispatch)
            {
                reason = ReasonUnsupportedTransportMode;
                return false;
            }

            return true;
        }

        public static LineDispatchSupport ComputeDispatchSupport(
            EntityManager entityManager,
            Entity line,
            Func<Entity, Entity> stop)
        {
            if (!IsDispatchTransportLine(entityManager, line, out string reason))
                return LineDispatchSupport.CreateUnsupported(reason);

            return RouteWaypointEndpointResolver.ComputeLineDispatchSupport(entityManager, line, stop);
        }

        public static bool TryGetTransportLineData(
            EntityManager entityManager,
            Entity line,
            out TransportLineData lineData)
        {
            lineData = default;
            if (line == Entity.Null || !entityManager.Exists(line))
                return false;

            if (entityManager.HasComponent<TransportLineData>(line))
            {
                lineData = entityManager.GetComponentData<TransportLineData>(line);
                return true;
            }

            if (!entityManager.HasComponent<PrefabRef>(line))
                return false;

            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !entityManager.HasComponent<TransportLineData>(prefab))
                return false;

            lineData = entityManager.GetComponentData<TransportLineData>(prefab);
            return true;
        }
    }
}
