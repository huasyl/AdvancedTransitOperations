using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal interface IPublicTransportWritePort
    {
        uint Frame { get; }
        PublicTransport ReadPublicTransport(Entity vehicle);
        void AppendPublicTransportWrite(Entity vehicle, PublicTransport value);
    }

    internal sealed class DispatchActions
    {
        private readonly IPublicTransportWritePort m_Host;

        public DispatchActions(IPublicTransportWritePort host)
        {
            m_Host = host;
        }

        public void CommitAssignedSlotHold(Entity vehicle, int slot, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            publicTransport.m_DepartureFrame = m_Host.Frame + 9999;
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        public void CommitPublicTransport(
            Entity vehicle,
            PublicTransport publicTransport,
            EntityCommandBuffer ecb)
        {
            m_Host.AppendPublicTransportWrite(vehicle, publicTransport);
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

        public void HoldDeparture(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            HoldDeparture(vehicle, ref publicTransport, nowFrame, ecb);
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

        public void ForceDepart(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            ForceDepart(vehicle, ref publicTransport, nowFrame, ecb);
        }

        public void CommitAssistLaunch(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            publicTransport.m_DepartureFrame = nowFrame > 0 ? nowFrame - 1 : 0;
            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        public void ReleaseDeparture(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            publicTransport.m_DepartureFrame = nowFrame > 0 ? nowFrame - 1 : 0;
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        internal static void ForceOfficialBoardingClose(ref PublicTransport publicTransport, uint nowFrame)
        {
            publicTransport.m_DepartureFrame = nowFrame > ModRuntimeHostSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                ? nowFrame - ModRuntimeHostSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                : 1;
            publicTransport.m_MinWaitingDistance = float.MaxValue;
            publicTransport.m_MaxBoardingDistance = float.MaxValue;
        }
    }
}
