using System;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class CommandHost
    {
        private readonly RuntimeVehicleLabels m_VehicleLabels;
        private readonly Func<Entity, Entity> m_ReadVehicleLine;
        private readonly Func<Entity, uint, bool> m_IsFreshPreparing;
        private readonly Action<Entity, uint> m_SetPreparing;
        private readonly Action<Entity> m_ClearAssistLaunchPending;
        private readonly Action<Entity> m_ClearBoardingGrace;
        private NativeHashMap<Entity, int> m_CachedWaypoint;
        private NativeHashMap<Entity, byte> m_LastEffectiveBoardingState;
        private NativeHashMap<Entity, byte> m_LastOfficialBoardingState;
        private NativeHashMap<Entity, Entity> m_StopSessionLine;
        private NativeHashMap<Entity, int> m_StopSessionWaypointIndex;
        private NativeHashMap<Entity, uint> m_StopSessionArrivalFrame;
        private NativeHashMap<Entity, uint> m_StopSessionBoardingChangeCount;
        private NativeHashMap<Entity, uint> m_DeparturePendingSinceFrame;
        private NativeHashSet<Entity> m_InvalidatedMidStopRecoveryPending;
        private NativeHashSet<Entity> m_Misfires;
        private NativeHashMap<Entity, uint> m_MisfireStartFrames;
        private NativeHashMap<Entity, uint> m_PreparingCooldown;

        public CommandHost(DispatchRuntimeSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_VehicleLabels = runtime.m_VehicleLabels;
            m_ReadVehicleLine = vehicle => runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null;
            m_IsFreshPreparing = (vehicle, nowFrame) => runtime.m_VehicleView.IsFreshPreparing(vehicle, nowFrame, DispatchRuntimeSystem.PREPARING_ROUTE_FIX_GRACE_FRAMES);
            m_SetPreparing = runtime.m_RuntimeController.SetPreparing;
            m_ClearAssistLaunchPending = runtime.m_RuntimeController.ClearAssistLaunchPending;
            m_ClearBoardingGrace = runtime.m_RuntimeController.ClearBoardingGrace;
            m_CachedWaypoint = runtime.m_CachedWpIdx;
            m_LastEffectiveBoardingState = runtime.m_LastEffectiveBoardingState;
            m_LastOfficialBoardingState = runtime.m_LastOfficialBoardingState;
            m_StopSessionLine = runtime.m_StopSessionLine;
            m_StopSessionWaypointIndex = runtime.m_StopSessionWaypointIndex;
            m_StopSessionArrivalFrame = runtime.m_StopSessionArrivalFrame;
            m_StopSessionBoardingChangeCount = runtime.m_StopSessionBoardingChangeCount;
            m_DeparturePendingSinceFrame = runtime.m_DeparturePendingSinceFrame;
            m_InvalidatedMidStopRecoveryPending = runtime.m_InvalidatedMidStopRecoveryPending;
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
            m_VehicleLabels.Set(vehicle, text);
        }

        public void SetLocalizedVehicleLabel(Entity vehicle, string key, string fallback, string suffix = "")
        {
            m_VehicleLabels.SetLocalized(vehicle, key, fallback, suffix);
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

        public void ClearBoardingObservation(Entity vehicle)
        {
            m_LastEffectiveBoardingState.Remove(vehicle);
            m_LastOfficialBoardingState.Remove(vehicle);
            m_StopSessionLine.Remove(vehicle);
            m_StopSessionWaypointIndex.Remove(vehicle);
            m_StopSessionArrivalFrame.Remove(vehicle);
            m_StopSessionBoardingChangeCount.Remove(vehicle);
            m_DeparturePendingSinceFrame.Remove(vehicle);
            m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
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
