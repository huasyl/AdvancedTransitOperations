using Game.Routes;
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
            int count = 0;
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) return 0;
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(vehicles[i].m_Vehicle);
                if (!m_Runtime.EntityManager.Exists(vehicle)) continue;
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
            }

            return count;
        }
    }
}
