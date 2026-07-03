using Game;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class LaunchActions
    {
        private readonly CommandHost m_Host;
        private readonly RouteWriter m_RouteWriter;

        public LaunchActions(CommandHost host, RouteWriter routeWriter)
        {
            m_Host = host;
            m_RouteWriter = routeWriter;
        }

        public void Launch(
            Entity vehicle,
            PublicTransport publicTransport,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            EntityCommandBuffer ecb)
        {
            m_Host.ClearAssistLaunchPending(vehicle);
            m_Host.ClearBoardingGrace(vehicle);
            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            publicTransport.m_DepartureFrame = m_Host.SimulationSystem.frameIndex - 1;
            if (m_RouteWriter.TryApplyLaunchSegmentPath(vehicle, ref publicTransport, ref target, waypoints, ecb))
                return;

            target.m_Target = waypoints[1].m_Waypoint;
            m_RouteWriter.Repath(vehicle, publicTransport, target, ecb);
        }

        public void EnsurePreparingRoute(
            Entity vehicle,
            ref PublicTransport publicTransport,
            ref Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            bool boarding,
            EntityCommandBuffer ecb)
        {
            Entity stationA = waypoints[0].m_Waypoint;
            bool wrongTarget = target.m_Target != stationA;
            bool driftedToMidStop = boarding && currentWaypointIndex > 0;
            if (!wrongTarget && !driftedToMidStop)
                return;

            uint nowFrame = m_Host.SimulationSystem.frameIndex;
            if (wrongTarget
                && !driftedToMidStop
                && m_Host.IsFreshPreparing(vehicle, nowFrame))
            {
                return;
            }
            if (m_Host.GetPreparingCooldown(vehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_Host.TryGetVehicleLine(vehicle, out Entity lineEnt)
                ? "线路" + lineEnt.Index
                : "线路?";
            string why = driftedToMidStop
                ? (currentWaypointIndex >= 0 ? ("偏航到 wp=" + currentWaypointIndex) : "偏航 boarding")
                : "目标不是始发站";

            m_Host.SetPreparing(vehicle, nowFrame);
            m_Host.ClearCachedWaypoint(vehicle);
            m_Host.ClearBoardingObservation(vehicle);
            m_Host.ClearMisfire(vehicle);
            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            publicTransport.m_DepartureFrame = nowFrame + 9999;
            target.m_Target = stationA;
            m_RouteWriter.Repath(vehicle, publicTransport, target, ecb);
            m_Host.SetPreparingCooldown(vehicle, nowFrame + DispatchRuntimeSystem.PREPARINGFIX_REPATH_COOLDOWN_FRAMES);

            m_Host.Log.Info("[PreparingFix] " + lineTag + " 车辆" + vehicle.Index
                + " " + why + "，重置去始发站 wp0=" + stationA.Index);
        }
    }
}
