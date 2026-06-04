using System;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class CommandHost
    {
        private readonly Action<Entity, string> m_SetVehicleLabel;
        private readonly Func<Entity, Entity> m_ReadVehicleLine;
        private readonly Func<Entity, uint, bool> m_IsFreshPreparing;
        private readonly Action<Entity, uint> m_SetPreparing;
        private readonly Action<Entity> m_ClearAssistLaunchPending;
        private readonly Action<Entity> m_ClearBoardingGrace;
        private NativeHashMap<Entity, int> m_CachedWaypoint;
        private NativeHashMap<Entity, bool> m_LastBoarding;
        private NativeHashSet<Entity> m_Misfires;
        private NativeHashMap<Entity, uint> m_MisfireStartFrames;
        private NativeHashMap<Entity, uint> m_PreparingCooldown;

        public CommandHost(DispatchRuntimeSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_SetVehicleLabel = runtime.m_VehicleLabels.Set;
            m_ReadVehicleLine = vehicle => runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null;
            m_IsFreshPreparing = (vehicle, nowFrame) => runtime.m_VehicleView.IsFreshPreparing(vehicle, nowFrame, DispatchRuntimeSystem.PREPARING_ROUTE_FIX_GRACE_FRAMES);
            m_SetPreparing = runtime.m_RuntimeController.SetPreparing;
            m_ClearAssistLaunchPending = runtime.m_RuntimeController.ClearAssistLaunchPending;
            m_ClearBoardingGrace = runtime.m_RuntimeController.ClearBoardingGrace;
            m_CachedWaypoint = runtime.m_CachedWpIdx;
            m_LastBoarding = runtime.m_LastBoarding;
            m_Misfires = runtime.m_BVMisfire;
            m_MisfireStartFrames = runtime.m_BVMisfireStartFrame;
            m_PreparingCooldown = runtime.m_PreparingFixCooldownUntil;
        }

        public EntityManager EntityManager { get; }
        public SimulationSystem SimulationSystem { get; }
        public TimedLogger Log { get; }

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        public void SetVehicleLabel(Entity vehicle, string text)
        {
            m_SetVehicleLabel(vehicle, text);
        }

        public bool TryGetVehicleLine(Entity vehicle, out Entity line)
        {
            line = m_ReadVehicleLine(vehicle);
            return line != Entity.Null;
        }

        public bool IsFreshPreparing(Entity vehicle, uint nowFrame)
        {
            return m_IsFreshPreparing(vehicle, nowFrame);
        }

        public void SetPreparing(Entity vehicle, uint nowFrame)
        {
            m_SetPreparing(vehicle, nowFrame);
        }

        public void ClearAssistLaunchPending(Entity vehicle)
        {
            m_ClearAssistLaunchPending(vehicle);
        }

        public void ClearBoardingGrace(Entity vehicle)
        {
            m_ClearBoardingGrace(vehicle);
        }

        public void ClearCachedWaypoint(Entity vehicle)
        {
            m_CachedWaypoint.Remove(vehicle);
        }

        public void ClearLastBoarding(Entity vehicle)
        {
            m_LastBoarding.Remove(vehicle);
        }

        public void ClearMisfire(Entity vehicle)
        {
            m_Misfires.Remove(vehicle);
            m_MisfireStartFrames.Remove(vehicle);
        }

        public bool GetPreparingCooldown(Entity vehicle, out uint frame)
        {
            return m_PreparingCooldown.TryGetValue(vehicle, out frame);
        }

        public void SetPreparingCooldown(Entity vehicle, uint frame)
        {
            if (frame == 0)
            {
                m_PreparingCooldown.Remove(vehicle);
                return;
            }

            m_PreparingCooldown[vehicle] = frame;
        }
    }
}
