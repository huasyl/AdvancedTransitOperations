using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum ExitMode
    {
        Dead,
        Live
    }

    internal readonly struct ExitContext
    {
        internal readonly Entity Vehicle;
        internal readonly Entity Line;
        internal readonly VehicleState State;
        internal readonly LifecycleKind Lifecycle;

        internal ExitContext(Entity vehicle, Entity line, VehicleState state, LifecycleKind lifecycle)
        {
            Vehicle = vehicle;
            Line = line;
            State = state;
            Lifecycle = lifecycle;
        }
    }

    internal sealed class VehicleExit
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        internal VehicleExit(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal ExitContext Release(Entity vehicle, ExitMode mode, string reason)
        {
            ExitContext context = Capture(vehicle);
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;

            if (mode == ExitMode.Live)
            {
                m_Runtime.m_CommandApplier.ReleaseExitHold(vehicle, context.Lifecycle, frame);
                m_Runtime.m_VehicleCache.RemoveVehicle(vehicle);
            }

            StopCancelResult cancelled = m_Runtime.m_StopRuntime.ReleaseVehicle(vehicle, frame);
            if (cancelled.Exists)
                m_Runtime.PublishStopFact(cancelled.Fact);

            bool rail = context.Lifecycle == LifecycleKind.Rail;
            m_Runtime.m_Observation.ClearVehicle(vehicle, rail);
            if (rail)
                ClearRailVehicle(vehicle, mode, reason);
            else
                m_Runtime.m_RoadEventSource.RemoveVehicle(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            ClearHostFields(vehicle, reason);
            m_Runtime.m_VehicleRegistry.Remove(vehicle);
            return context;
        }

        private void ClearRailVehicle(Entity vehicle, ExitMode mode, string reason)
        {
            m_Runtime.TrackProjection.ClearVehicle(vehicle);
            m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, reason);
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_BoardingFirstFrameGuardState.Remove(vehicle);
            m_Runtime.Bypass.ClearVehicle(vehicle, reason);
            if (mode == ExitMode.Dead)
            {
                List<Entity> affected = m_Runtime.Bypass.ForgetBlocker(vehicle);
                if (affected != null)
                {
                    for (int i = 0; i < affected.Count; i++)
                        m_Runtime.m_RuntimeFramePlan.AddStage(affected[i], RuntimeStageMask.Bypass);
                }
            }
            m_Runtime.m_RailEventSource.RemoveVehicle(vehicle);
            m_Runtime.m_RailEtaService?.CancelTargetRequests(vehicle, reason);
        }

        private ExitContext Capture(Entity vehicle)
        {
            Entity line = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine)
                ? registeredLine
                : Entity.Null;
            VehicleState state = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState registeredState)
                ? registeredState
                : default;
            LifecycleKind lifecycle = m_Runtime.m_VehicleWorksets.TryGetMode(vehicle, out TransitMode mode)
                ? TransportModeProfile.GetProfile(mode).Lifecycle
                : LifecycleKind.Unknown;
            return new ExitContext(vehicle, line, state, lifecycle);
        }

        private void ClearHostFields(Entity vehicle, string reason)
        {
            m_Runtime.m_UICache.Remove(vehicle);
            m_Runtime.m_CachedWpIdx.Remove(vehicle);
            m_Runtime.m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Runtime.m_RuntimeEngine.ClearAssistLaunchPending(vehicle);
            m_Runtime.m_SpawnIntentTrace?.Remove(vehicle);
            m_Runtime.m_RuntimeLog.ClearVehicle(vehicle);
            m_Runtime.m_StationContextQuery.RemoveVehicle(vehicle);
            m_Runtime.m_CommandApplier.RemoveRetireHandoff(vehicle);
            m_Runtime.m_CommandApplier.FlushRetireShadowSnapshots(vehicle, reason);
            m_Runtime.m_CommandApplier.ResetRetireShadowSnapshots(vehicle);
            m_Runtime.m_CommandApplier.RemoveRoadCommandLog(vehicle);
        }
    }
}
