using System.Collections.Generic;
using RapidTransitMod.Dispatch.Lines;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineStructureInvalidator
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, PendingInvalidation> m_Pending = new Dictionary<Entity, PendingInvalidation>();
        private readonly Dictionary<Entity, PendingRoadInvalidation> m_PendingRoadLines = new Dictionary<Entity, PendingRoadInvalidation>();

        private readonly struct PendingInvalidation
        {
            public readonly Entity Line;
            public readonly ulong OldSignature;
            public readonly ulong NewSignature;
            public readonly int OldAtomCount;
            public readonly int NewAtomCount;

            public PendingInvalidation(
                Entity line,
                ulong oldSignature,
                ulong newSignature,
                int oldAtomCount,
                int newAtomCount)
            {
                Line = line;
                OldSignature = oldSignature;
                NewSignature = newSignature;
                OldAtomCount = oldAtomCount;
                NewAtomCount = newAtomCount;
            }

            public PendingInvalidation WithLatest(ulong newSignature, int newAtomCount)
            {
                return new PendingInvalidation(Line, OldSignature, newSignature, OldAtomCount, newAtomCount);
            }
        }

        private readonly struct PendingRoadInvalidation
        {
            internal readonly Entity Line;
            internal readonly LineProfile.RoadRouteSnapshot OldRoute;
            internal readonly LineProfile.RoadRouteSnapshot NewRoute;

            internal PendingRoadInvalidation(
                Entity line,
                LineProfile.RoadRouteSnapshot oldRoute,
                LineProfile.RoadRouteSnapshot newRoute)
            {
                Line = line;
                OldRoute = oldRoute;
                NewRoute = newRoute;
            }

            internal PendingRoadInvalidation WithLatest(LineProfile.RoadRouteSnapshot newRoute)
            {
                return new PendingRoadInvalidation(Line, OldRoute, newRoute);
            }
        }

        internal LineStructureInvalidator(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal void Request(Entity line, ulong oldSignature, ulong newSignature, int oldAtomCount, int newAtomCount)
        {
            if (line == Entity.Null)
                return;
            if (!m_Runtime.m_SystemReady)
                return;

            if (m_Pending.TryGetValue(line, out PendingInvalidation pending))
            {
                m_Pending[line] = pending.WithLatest(newSignature, newAtomCount);
                return;
            }

            m_Pending[line] = new PendingInvalidation(line, oldSignature, newSignature, oldAtomCount, newAtomCount);
        }

        internal void RequestRoadRoute(
            Entity line,
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            if (line == Entity.Null || oldRoute == null || newRoute == null || !m_Runtime.m_SystemReady)
                return;

            if (m_PendingRoadLines.TryGetValue(line, out PendingRoadInvalidation pending))
            {
                m_PendingRoadLines[line] = pending.WithLatest(newRoute);
                return;
            }

            m_PendingRoadLines[line] = new PendingRoadInvalidation(line, oldRoute, newRoute);
        }

        internal void Drain()
        {
            if (m_Pending.Count == 0 && m_PendingRoadLines.Count == 0)
                return;

            List<PendingInvalidation> pending = new List<PendingInvalidation>(m_Pending.Values);
            m_Pending.Clear();

            List<PendingRoadInvalidation> roadLines = new List<PendingRoadInvalidation>(m_PendingRoadLines.Values);
            m_PendingRoadLines.Clear();

            if (pending.Count > 0)
            {
                m_Runtime.m_LineTimes.Clear();
                m_Runtime.m_LineMileage.Clear();
                m_Runtime.m_LineView.Clear();
                m_Runtime.m_TrackProjection.ClearLineRunningVehicleSnapshots();
                m_Runtime.m_StationContextQuery.Clear();
            }

            for (int i = 0; i < pending.Count; i++)
                DrainLine(pending[i]);
            for (int i = 0; i < roadLines.Count; i++)
                DrainRoadLine(roadLines[i]);
        }

        private void DrainRoadLine(PendingRoadInvalidation pending)
        {
            Entity line = pending.Line;
            RapidTransitMod.PassengerFlow.Runtime.Current?.InvalidateAnchors(line);
            m_Runtime.m_Observation.InvalidateBusRoute(line, pending.OldRoute, pending.NewRoute);
            m_Runtime.m_LineTimes.InvalidateLine(line);
            if (RoadEntryChanged(pending.OldRoute, pending.NewRoute))
                m_Runtime.m_Observation.InvalidateDispatchTiming(line);
            m_Runtime.m_RoadEventSource.InvalidateLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);

            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line)
                    {
                        continue;
                    }

                    m_Runtime.m_RoadEventSource.CommitWaypoint(vehicle, -1);
                    m_Runtime.m_RouteProgress.Remove(vehicle);
                    m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
                    RapidTransitMod.PassengerFlow.Runtime.Current?.RemoveVehicle(vehicle);
                    m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Stop);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private static bool RoadEntryChanged(
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            Entity oldWaypoint = oldRoute != null && oldRoute.Waypoints.Length > 0
                ? oldRoute.Waypoints[0]
                : Entity.Null;
            Entity newWaypoint = newRoute != null && newRoute.Waypoints.Length > 0
                ? newRoute.Waypoints[0]
                : Entity.Null;
            if (oldWaypoint != newWaypoint)
                return true;

            Entity oldStop = FirstResolvedStop(oldRoute);
            Entity newStop = FirstResolvedStop(newRoute);
            return oldStop != newStop;
        }

        private static Entity FirstResolvedStop(LineProfile.RoadRouteSnapshot route)
        {
            if (route == null)
                return Entity.Null;

            for (int i = 0; i < route.Stops.Length; i++)
            {
                if (route.Stops[i] != Entity.Null)
                    return route.Stops[i];
            }

            return Entity.Null;
        }

        private void DrainLine(PendingInvalidation pending)
        {
            Entity line = pending.Line;
            m_Runtime.m_RailEventSource.InvalidateLine(line);
            m_Runtime.m_TrackModel.InvalidateWaypointIndexLookup(line);
            m_Runtime.m_LapCache.RemoveLine(line);
            m_Runtime.m_Observation.RemoveLine(line);
            m_Runtime.m_Observation.InvalidateSliceLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);
            m_Runtime.m_Bypass.ClearLine(line);

            int clearedVehicles = 0;
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line)
                    {
                        continue;
                    }

                    ClearVehiclePosition(vehicle);
                    clearedVehicles++;
                }
            }
            finally
            {
                vehicles.Dispose();
            }

            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                m_Runtime.log.Info("[LineStructureInvalidated] line=" + line.Index
                    + " oldSig=" + pending.OldSignature
                    + " newSig=" + pending.NewSignature
                    + " oldAtoms=" + pending.OldAtomCount
                    + " newAtoms=" + pending.NewAtomCount
                    + " vehicles=" + clearedVehicles
                    + " clearLineTimes=1"
                    + " clearLineMileage=1"
                    + " clearSlices=1"
                    + " clearBypassLine=1"
                    + " clearDispatchCache=1"
                    + " clearLapCache=1"
                    + " clearLineProfile=1"
                    + " clearVehiclePosition=" + clearedVehicles);
            }
        }

        private void ClearVehiclePosition(Entity vehicle)
        {
            m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, -1);
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_TrackProjection.ClearVehicle(vehicle);
            m_Runtime.m_ObsPersist.ClearLap(vehicle);
            m_Runtime.m_Observation.ClearVehicleSlices(vehicle);
            m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
            m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Stop);
        }
    }
}
