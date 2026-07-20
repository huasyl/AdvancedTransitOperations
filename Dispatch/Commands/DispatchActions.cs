using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class DispatchActions
    {
        private readonly CommandHost m_Host;

        public DispatchActions(CommandHost host)
        {
            m_Host = host;
        }

        public void CommitAssignedSlotHold(Entity vehicle, int slot, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            publicTransport.m_DepartureFrame = m_Host.SimulationSystem.frameIndex + 9999;
            CommitPublicTransport(vehicle, publicTransport, ecb);
            m_Host.SetLocalizedVehicleLabel(vehicle, "Holding", "候车", " " + DispatchRuntimeSystem.SlotStr(slot));
        }

        public void CommitPublicTransport(
            Entity vehicle,
            PublicTransport publicTransport,
            EntityCommandBuffer ecb)
        {
            ecb.SetComponent(vehicle, publicTransport);
        }

        public void HoldDeparture(
            Entity vehicle,
            ref PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            publicTransport.m_DepartureFrame = nowFrame + 9999;
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        public void ForceDepart(
            Entity vehicle,
            ref PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            ForceOfficialBoardingClose(ref publicTransport, nowFrame);
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        internal static void ForceOfficialBoardingClose(ref PublicTransport publicTransport, uint nowFrame)
        {
            publicTransport.m_DepartureFrame = nowFrame > DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                ? nowFrame - DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                : 1;
            publicTransport.m_MinWaitingDistance = float.MaxValue;
            publicTransport.m_MaxBoardingDistance = float.MaxValue;
        }
    }
}
