using Game;
using Game.Common;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed partial class BoardingFirstFrameGuardSystem : GameSystemBase
    {
        private const uint GuardFrames = 5;

        private SimulationSystem m_SimulationSystem = null!;
        private ComponentLookup<PublicTransport> m_PublicTransportLookup;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PublicTransportLookup = GetComponentLookup<PublicTransport>(isReadOnly: false);
        }

        protected override void OnUpdate()
        {
            DispatchRuntimeSystem runtime = DispatchRuntimeSystem.Instance;
            if (runtime == null
                || runtime.m_VehicleView == null
                || !runtime.m_BoardingFirstFrameGuardState.IsCreated)
            {
                return;
            }

            uint nowFrame = m_SimulationSystem.frameIndex;
            m_PublicTransportLookup.Update(this);
            NativeArray<Entity> vehicles = runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_PublicTransportLookup.HasComponent(vehicle))
                    {
                        runtime.m_BoardingFirstFrameGuardState.Remove(vehicle);
                        continue;
                    }

                    PublicTransport publicTransport = m_PublicTransportLookup[vehicle];
                    bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
                    byte current = boarding ? (byte)1 : (byte)0;
                    bool wasBoarding = runtime.m_BoardingFirstFrameGuardState.TryGetValue(vehicle, out byte previous)
                        && previous != 0;
                    runtime.m_BoardingFirstFrameGuardState[vehicle] = current;

                    if (!boarding
                        || wasBoarding
                        || !runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                        || state != VehicleState.Running)
                    {
                        continue;
                    }

                    if (!IsOfficialBoardingAtTargetStop(vehicle))
                        continue;

                    uint protectedUntil = nowFrame + GuardFrames;
                    if (publicTransport.m_DepartureFrame > protectedUntil)
                        continue;

                    publicTransport.m_DepartureFrame = protectedUntil;
                    m_PublicTransportLookup[vehicle] = publicTransport;
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private bool IsOfficialBoardingAtTargetStop(Entity vehicle)
        {
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Entity targetWaypoint = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (targetWaypoint == Entity.Null || !EntityManager.HasComponent<Connected>(targetWaypoint))
                return false;

            Entity targetStop = EntityManager.GetComponentData<Connected>(targetWaypoint).m_Connected;
            if (targetStop == Entity.Null || !EntityManager.HasComponent<BoardingVehicle>(targetStop))
                return false;

            return EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle == vehicle;
        }
    }
}
