using System;
using Game.Simulation;
using Game.Common;
using Game.Pathfind;
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
        private readonly RailEventSource m_RailEvents;
        private readonly RuntimeWorksets m_Worksets;
        private readonly StopRuntime m_StopRuntime;
        private NativeHashMap<Entity, int> m_CachedWaypoint;
        private NativeHashSet<Entity> m_Misfires;
        private NativeHashMap<Entity, uint> m_MisfireStartFrames;
        private NativeHashMap<Entity, uint> m_PreparingCooldown;

        public CommandHost(ModRuntimeHostSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_VehicleLabels = runtime.m_VehicleLabels;
            m_ReadVehicleLine = vehicle => runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null;
            m_IsFreshPreparing = (vehicle, nowFrame) => runtime.m_VehicleView.IsFreshPreparing(vehicle, nowFrame, ModRuntimeHostSystem.PREPARING_ROUTE_FIX_GRACE_FRAMES);
            m_SetPreparing = runtime.m_RuntimeEngine.SetPreparing;
            m_ClearAssistLaunchPending = runtime.m_RuntimeEngine.ClearAssistLaunchPending;
            m_ClearBoardingGrace = runtime.m_RuntimeEngine.ClearBoardingGrace;
            m_RailEvents = runtime.m_RailEventSource;
            m_Worksets = runtime.m_RuntimeWorksets;
            m_StopRuntime = runtime.m_StopRuntime;
            m_CachedWaypoint = runtime.m_CachedWpIdx;
            m_Misfires = runtime.m_BVMisfire;
            m_MisfireStartFrames = runtime.m_BVMisfireStartFrame;
            m_PreparingCooldown = runtime.m_PreparingFixCooldownUntil;
        }

        public EntityManager EntityManager { get; }
        public SimulationSystem SimulationSystem { get; }
        public TimedLogger Log { get; }

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return m_RailEvents.ReadPublicTransport(vehicle);
        }

        public Target ReadTarget(Entity vehicle) => m_RailEvents.ReadTarget(vehicle);
        public PathOwner ReadPath(Entity vehicle) => m_RailEvents.ReadPath(vehicle);

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport value)
        {
            m_RailEvents.AppendPublicTransportWrite(vehicle, value, SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendTargetWrite(Entity vehicle, Target value)
        {
            m_RailEvents.AppendTargetWrite(vehicle, value, SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, int pathElementCount)
        {
            m_RailEvents.AppendPathWrite(vehicle, value, hasPathElements, pathElementCount, 0UL, SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, DynamicBuffer<PathElement> path)
        {
            m_RailEvents.AppendPathWrite(
                vehicle,
                value,
                hasPathElements,
                path.Length,
                RailEventSource.PathSignature(path),
                SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
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
            PassengerFlow.Runtime.Current?.CancelStop(vehicle);
            m_StopRuntime.ClearBoardingObservation(vehicle);
        }

        public void ClearMisfire(Entity vehicle)
        {
            m_Misfires.Remove(vehicle);
            m_MisfireStartFrames.Remove(vehicle);
            m_Worksets.ClearDeadline(vehicle, DeadlineKind.BvMisfire);
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
                m_Worksets.ClearDeadline(vehicle, DeadlineKind.PreparingCooldown);
                return;
            }

            m_PreparingCooldown[vehicle] = frame;
            m_Worksets.SetDeadline(vehicle, DeadlineKind.PreparingCooldown, frame);
        }
    }
}
