using System.Collections.Generic;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct BypassControlResult
    {
        public readonly bool Evaluated;
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly bool HadLatchedYield;
        public readonly bool ShouldHold;
        public readonly Entity Blocker;
        public readonly bool CanClearAfterExit;
        public readonly string ReleaseReason;

        public BypassControlResult(
            bool evaluated,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            bool hadLatchedYield,
            bool shouldHold,
            Entity blocker,
            bool canClearAfterExit,
            string releaseReason)
        {
            Evaluated = evaluated;
            Vehicle = vehicle;
            Line = line;
            WaypointIndex = waypointIndex;
            HadLatchedYield = hadLatchedYield;
            ShouldHold = shouldHold;
            Blocker = blocker;
            CanClearAfterExit = canClearAfterExit;
            ReleaseReason = releaseReason;
        }

        public bool ShouldRelease => !string.IsNullOrWhiteSpace(ReleaseReason);
    }

    internal sealed class ControlService
    {
        private readonly IControlContext m_Runtime;
        private readonly AdmissionService m_Admission;
        private readonly Dictionary<Entity, string> m_HoldFrameLogCache = new Dictionary<Entity, string>();

        internal ControlService(IControlContext runtime, AdmissionService admission)
        {
            m_Runtime = runtime;
            m_Admission = admission;
        }

        internal void Clear()
        {
            m_HoldFrameLogCache.Clear();
        }

        internal void RemoveVehicleLogs(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_HoldFrameLogCache.Remove(vehicle);
        }

        internal BypassControlResult Update(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            uint nowFrame)
        {
            if (m_Admission.TryGetBypassHoldSkipped(vehicle, out Entity skippedBlocker))
            {
                return new BypassControlResult(
                    true,
                    vehicle,
                    line,
                    waypointIndex,
                    false,
                    false,
                    skippedBlocker,
                    true,
                    null);
            }

            bool hadLatchedYield = m_Admission.TryGetLatchedBlocker(vehicle, out _);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex <= 0
                || (!boarding && !hadLatchedYield))
            {
                if (!boarding && !hadLatchedYield)
                    m_Admission.ClearInactive(vehicle);
                return new BypassControlResult(
                    false,
                    vehicle,
                    line,
                    waypointIndex,
                    hadLatchedYield,
                    false,
                    Entity.Null,
                    true,
                    null);
            }

            BypassDecisionResult decision = m_Admission.EvaluateDepartureGate(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                nowFrame);
            Entity blocker = m_Admission.FindBlocker(decision);
            string releaseReason = null;
            if (m_Admission.CanRelease(decision))
            {
                releaseReason = !string.IsNullOrWhiteSpace(decision.ReleaseReason)
                    ? decision.ReleaseReason
                    : (decision.CanClearAfterExit ? "已越过当前待避站出口" : "待避条件消失");
            }

            return new BypassControlResult(
                decision.Evaluated,
                vehicle,
                line,
                waypointIndex,
                decision.HadLatchedYield,
                decision.ShouldHold,
                blocker,
                decision.CanClearAfterExit,
                releaseReason);
        }

        internal void LogHoldFrame(
            BypassControlResult control,
            bool boarding,
            uint departureFrame,
            uint nowFrame)
        {
            if (!RtLog.VerboseEnabled
                || (!control.HadLatchedYield && !control.ShouldHold))
                return;

            string holdFrameAction = control.ShouldHold
                ? "hold"
                : "release";
            m_Runtime.LogVehicleStateOnce(
                m_HoldFrameLogCache,
                control.Vehicle,
                "frame|" + control.WaypointIndex
                    + "|" + boarding
                    + "|" + control.HadLatchedYield
                    + "|" + control.ShouldHold
                    + "|" + control.CanClearAfterExit
                    + "|" + control.Blocker.Index
                    + "|" + holdFrameAction
                    + "|" + (control.ReleaseReason ?? "-"),
                "[待避压车帧] vehicle=" + control.Vehicle.Index
                    + " line=" + control.Line.Index
                    + " wp=" + control.WaypointIndex
                    + " boarding=" + boarding
                    + " latched=" + control.HadLatchedYield
                    + " shouldHold=" + control.ShouldHold
                    + " blocker=" + control.Blocker.Index
                    + " canClear=" + control.CanClearAfterExit
                    + " depBefore=" + departureFrame
                    + " frame=" + nowFrame
                    + " action=" + holdFrameAction
                    + (!string.IsNullOrWhiteSpace(control.ReleaseReason) ? " reason=" + control.ReleaseReason : string.Empty));
        }

        internal void Hold(
            BypassControlResult control,
            ref PublicTransport publicTransport,
            EntityCommandBuffer ecb,
            DynamicBuffer<RouteWaypoint> waypoints,
            string lineTag,
            uint nowFrame)
        {
            if (!control.ShouldHold)
                return;

            publicTransport.m_DepartureFrame = nowFrame + 9999;
            m_Runtime.RecordPublicTransportWrite(control.Vehicle, publicTransport);
            ecb.SetComponent(control.Vehicle, publicTransport);
            Entity holdStation = m_Runtime.ResolveStation(waypoints, control.WaypointIndex);
            if (control.Vehicle != Entity.Null
                && control.Blocker != Entity.Null
                && (!m_Admission.TryGetLatchedBlocker(control.Vehicle, out Entity previousBlocker)
                    || previousBlocker != control.Blocker))
            {
                m_Admission.SetBlocker(control.Vehicle, control.Blocker);
                m_Runtime.RecordHold(control.Vehicle, control.Blocker, lineTag, holdStation, control.WaypointIndex, "运行中");
            }
            m_Runtime.TriggerWaiting(control.Vehicle, control.Line, waypoints, control.WaypointIndex);
        }

        internal void Release(BypassControlResult control)
        {
            if (!control.ShouldRelease)
                return;

            Entity blocker = control.Blocker;
            if (blocker == Entity.Null)
                m_Admission.TryGetLatchedBlocker(control.Vehicle, out blocker);

            m_Admission.ClearBlocker(control.Vehicle);
            m_Admission.RemoveCadence(control.Vehicle);
            m_Admission.RemoveEpisode(control.Vehicle);
            m_Runtime.RecordRelease(control.Vehicle, blocker, control.ReleaseReason);
        }

        internal void Apply(
            BypassControlResult control,
            bool boarding,
            ref PublicTransport publicTransport,
            EntityCommandBuffer ecb,
            DynamicBuffer<RouteWaypoint> waypoints,
            string lineTag,
            uint nowFrame)
        {
            LogHoldFrame(control, boarding, publicTransport.m_DepartureFrame, nowFrame);

            if (control.WaypointIndex > 0 && control.ShouldHold)
            {
                Hold(control, ref publicTransport, ecb, waypoints, lineTag, nowFrame);
            }
            else if (control.ShouldRelease)
            {
                Release(control);
            }
        }
    }
}
