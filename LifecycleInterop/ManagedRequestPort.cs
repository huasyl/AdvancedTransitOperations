using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class ManagedRequestPort
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public ManagedRequestPort(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public bool IsManagedLine(Entity line)
        {
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && !m_Runtime.EntityManager.HasComponent<Disabled>(line)
                && m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch());
        }

        public bool TryGetSpawnTarget(Entity line, out int targetCount)
        {
            targetCount = 0;
            return line != Entity.Null
                && m_Runtime.m_SpawningLines.IsCreated
                && m_Runtime.m_SpawningLines.TryGetValue(line, out targetCount);
        }

        public int CountActiveVehicles(Entity line)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line))
                return 0;

            BufferLookup<RouteVehicle> routeVehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            return m_Runtime.m_LineVehicles.Count(line, routeVehicles);
        }

        public bool IsParkedSentinel(Entity request, Entity line)
        {
            if (request == Entity.Null
                || !m_Runtime.EntityManager.Exists(request)
                || !m_Runtime.EntityManager.HasComponent<RtVehicleRequestSentinel>(request)
                || !m_Runtime.EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = m_Runtime.EntityManager.GetComponentData<TransportVehicleRequest>(request);
            return line == Entity.Null || vehicleRequest.m_Route == line;
        }

        public bool IsSpawnPermit(Entity request, Entity line)
        {
            if (request == Entity.Null
                || !m_Runtime.EntityManager.Exists(request)
                || !m_Runtime.EntityManager.HasComponent<RtSpawnPermitRequest>(request)
                || !m_Runtime.EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = m_Runtime.EntityManager.GetComponentData<TransportVehicleRequest>(request);
            return line == Entity.Null || vehicleRequest.m_Route == line;
        }

        public bool ShouldDestroyOfficial(Entity request, Entity line)
        {
            if (!IsManagedLine(line))
                return false;

            if (IsParkedSentinel(request, line) || IsSpawnPermit(request, line))
                return false;

            if (!m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line))
                return true;

            DynamicBuffer<RouteVehicle> routeVehicles = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
            uint nowFrame = m_Runtime.m_SimulationSystem != null ? m_Runtime.m_SimulationSystem.frameIndex : 0u;
            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(routeVehicles[i].m_Vehicle);
                if (m_Runtime.m_VehicleView.IsFreshPreparing(vehicle, nowFrame, ModRuntimeHostSystem.PREPARING_ROUTE_FIX_GRACE_FRAMES))
                    return false;
            }

            return true;
        }

        public bool ShouldDestroyOfficial(Entity line)
        {
            return ShouldDestroyOfficial(Entity.Null, line);
        }
    }
}
