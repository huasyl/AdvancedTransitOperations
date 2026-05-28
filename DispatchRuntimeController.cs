using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class DispatchRuntimeController
    {
        private readonly VehicleRuntimeRegistry m_Vehicles;

        public DispatchRuntimeController(VehicleRuntimeRegistry vehicles)
        {
            m_Vehicles = vehicles;
        }

        public void Adopt(Entity vehicle, Entity line, VehicleState state, uint nowFrame, uint? dispatchFrame)
        {
            m_Vehicles.Track(vehicle, line);
            m_Vehicles.SetState(vehicle, state);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.ClearBoardingGrace(vehicle);

            if (state == VehicleState.Preparing)
                m_Vehicles.SetPreparing(vehicle, nowFrame);
            else
                m_Vehicles.ClearPreparing(vehicle);

            if (dispatchFrame.HasValue)
                m_Vehicles.SetDispatch(vehicle, dispatchFrame.Value);
            else
                m_Vehicles.ClearDispatch(vehicle);
        }

        public void Retire(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Retiring);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearDispatch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.ClearBoardingGrace(vehicle);
        }

        public void Hold(Entity vehicle, uint readyFrame)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.SetReady(vehicle, readyFrame);
        }

        public void HoldFromIdle(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void RecoverToHolding(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void Launch(Entity vehicle, int slot, uint nowFrame, uint cooldownUntil)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.SetLaunch(vehicle, nowFrame);
            m_Vehicles.SetCooldown(vehicle, cooldownUntil);
            m_Vehicles.SetSlot(vehicle, slot);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void Run(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void RestoreHold(Entity vehicle, int targetMin)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.SetTarget(vehicle, targetMin);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
        }

        public void RestoreRun(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void RecoverToIdle(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Idle);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.SetIdle(vehicle, nowFrame);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void ArriveIdle(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Idle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void Reevaluate(Entity vehicle)
        {
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearDispatch(vehicle);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void Target(Entity vehicle, int targetMin)
        {
            m_Vehicles.SetTarget(vehicle, targetMin);
        }

        public void ReleaseTarget(Entity vehicle)
        {
            m_Vehicles.ClearTarget(vehicle);
        }

        public void MarkInbound(Entity vehicle)
        {
            m_Vehicles.MarkInbound(vehicle);
        }

        public void ClearInbound(Entity vehicle)
        {
            m_Vehicles.ClearInbound(vehicle);
        }

        public void SetPreparing(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetPreparing(vehicle, nowFrame);
        }

        public void SetDispatch(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetDispatch(vehicle, nowFrame);
        }

        public void ClearDispatch(Entity vehicle)
        {
            m_Vehicles.ClearDispatch(vehicle);
        }

        public void SetReady(Entity vehicle, uint frame)
        {
            m_Vehicles.SetReady(vehicle, frame);
        }

        public void ClearReady(Entity vehicle)
        {
            m_Vehicles.ClearReady(vehicle);
        }

        public void ClearOriginCandidate(Entity vehicle)
        {
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void SetOriginCandidate(Entity vehicle, uint frame)
        {
            m_Vehicles.SetOriginCandidate(vehicle, frame);
        }

        public void SetBoardingGrace(Entity vehicle, uint frame)
        {
            m_Vehicles.SetBoardingGrace(vehicle, frame);
        }

        public void ClearBoardingGrace(Entity vehicle)
        {
            m_Vehicles.ClearBoardingGrace(vehicle);
        }

        public void SetIdle(Entity vehicle, uint frame)
        {
            m_Vehicles.SetIdle(vehicle, frame);
        }

        public void ClearIdle(Entity vehicle)
        {
            m_Vehicles.ClearIdle(vehicle);
        }
    }
}
