using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod
{
    internal interface IBypassControlRuntime
    {
        void Hold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex);

        void Release(Entity vehicle, string reason);

        Entity ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);

        void TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
    }

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

    internal sealed class BypassControl
    {
        private readonly IBypassControlRuntime m_Runtime;
        private readonly BypassDecision m_Decision;

        public BypassControl(IBypassControlRuntime runtime, BypassDecision decision)
        {
            m_Runtime = runtime;
            m_Decision = decision;
        }

        public BypassControlResult Update(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            uint nowFrame)
        {
            bool hadLatchedYield = m_Decision.TryGetLatchedBlocker(vehicle, out _);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex <= 0
                || (!boarding && !hadLatchedYield))
            {
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

            BypassDecisionResult decision = m_Decision.Evaluate(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                nowFrame);
            Entity blocker = m_Decision.FindBlocker(decision);
            string releaseReason = null;
            if (m_Decision.CanRelease(decision))
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

        public void Hold(
            BypassControlResult control,
            ref PublicTransport pt,
            EntityCommandBuffer ecb,
            DynamicBuffer<RouteWaypoint> waypoints,
            string lineTag,
            uint nowFrame)
        {
            if (!control.ShouldHold)
                return;

            pt.m_DepartureFrame = nowFrame + 9999;
            ecb.SetComponent(control.Vehicle, pt);
            Entity holdStation = m_Runtime.ResolveStation(waypoints, control.WaypointIndex);
            m_Runtime.Hold(control.Vehicle, control.Blocker, lineTag, holdStation, control.WaypointIndex);
            m_Runtime.TriggerWaiting(control.Vehicle, control.Line, waypoints, control.WaypointIndex);
        }

        public void Release(BypassControlResult control)
        {
            if (!control.ShouldRelease)
                return;

            m_Runtime.Release(control.Vehicle, control.ReleaseReason);
        }
    }

    public partial class DispatchRuntimeSystem : IBypassControlRuntime
    {
        void IBypassControlRuntime.Hold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex)
        {
            SetBypassYieldState(vehicle, blocker, lineTag, "运行中", holdStation, waypointIndex);
        }

        void IBypassControlRuntime.Release(Entity vehicle, string reason)
        {
            ClearBypassYieldState(vehicle, reason);
        }

        Entity IBypassControlRuntime.ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            return waypointIndex >= 0 && waypointIndex < waypoints.Length
                ? ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint)
                : Entity.Null;
        }

        void IBypassControlRuntime.TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            HandleBroadcastBypassWaitingTrigger(vehicle, route, waypoints, waypointIndex);
        }
    }
}
