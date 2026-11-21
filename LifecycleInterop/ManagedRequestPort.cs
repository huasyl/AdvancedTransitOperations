using Game.Simulation;
using Unity.Entities;

namespace RapidTransitMod
{
    internal enum ManagedRequestLineState : byte
    {
        Unmanaged,
        WaitingStable,
        Paused,
        Managed
    }

    internal sealed class ManagedRequestPort
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public ManagedRequestPort(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public bool IsManagedLine(Entity line)
        {
            return GetLineState(line) == ManagedRequestLineState.Managed;
        }

        public ManagedRequestLineState GetLineState(Entity line)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line))
                return ManagedRequestLineState.Unmanaged;
            if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch()))
                return ManagedRequestLineState.Unmanaged;
            return m_Runtime.m_LineServiceState.GetGate(line) switch
            {
                LineServiceGate.Operational => ManagedRequestLineState.Managed,
                LineServiceGate.Paused => ManagedRequestLineState.Paused,
                _ => ManagedRequestLineState.WaitingStable
            };
        }

        public int SpawnDeficit(Entity line)
        {
            return m_Runtime.m_LineSpawnControl?.SpawnDeficit(line) ?? 0;
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
    }
}
