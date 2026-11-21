using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineVehicles
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public LineVehicles(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public int Count(Entity line, BufferLookup<RouteVehicle> rvBuffers)
        {
            return Count(line, rvBuffers, out _);
        }

        public int Count(Entity line, BufferLookup<RouteVehicle> rvBuffers, out int unregistered)
        {
            int count = 0;
            unregistered = 0;
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) return 0;
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(vehicles[i].m_Vehicle);
                if (!m_Runtime.EntityManager.Exists(vehicle)) continue;
                if (!m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                    || m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != line) continue;
                if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                {
                    continue;
                }
                if (m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    && state == VehicleState.Retiring)
                {
                    continue;
                }

                count++;
                if (!m_Runtime.m_VehicleView.Contains(vehicle)
                    && m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                    && (m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State
                        & (PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute)) == 0)
                {
                    unregistered++;
                }
            }

            return count;
        }
    }
}
