using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Dispatch.Lines;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineStructureInvalidator
    {
        private const uint LayoutRetryFrames = 16u;
        private const byte LayoutRetryLimit = 3;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, PendingInvalidation> m_Pending = new Dictionary<Entity, PendingInvalidation>();
        private readonly Dictionary<Entity, PendingRoadInvalidation> m_PendingRoadLines = new Dictionary<Entity, PendingRoadInvalidation>();
        private uint m_NextLayoutRetryFrame;

        private readonly struct PendingInvalidation
        {
            public readonly Entity Line;
            public readonly string LineId;
            public readonly string Mode;
            public readonly ulong OldSignature;
            public readonly ulong NewSignature;
            public readonly int OldAtomCount;
            public readonly int NewAtomCount;
            public readonly uint NextRetryFrame;
            public readonly byte RetryCount;

            public PendingInvalidation(
                Entity line,
                string lineId,
                string mode,
                ulong oldSignature,
                ulong newSignature,
                int oldAtomCount,
                int newAtomCount,
                uint nextRetryFrame = 0,
                byte retryCount = 0)
            {
                Line = line;
                LineId = lineId ?? string.Empty;
                Mode = mode ?? string.Empty;
                OldSignature = oldSignature;
                NewSignature = newSignature;
                OldAtomCount = oldAtomCount;
                NewAtomCount = newAtomCount;
                NextRetryFrame = nextRetryFrame;
                RetryCount = retryCount;
            }

            public PendingInvalidation WithLatest(ulong newSignature, int newAtomCount)
            {
                return new PendingInvalidation(
                    Line, LineId, Mode, OldSignature, newSignature, OldAtomCount, newAtomCount);
            }

            public PendingInvalidation WithRetry(uint frame)
            {
                byte count = (byte)(RetryCount + 1);
                return new PendingInvalidation(
                    Line,
                    LineId,
                    Mode,
                    OldSignature,
                    NewSignature,
                    OldAtomCount,
                    NewAtomCount,
                    frame + LayoutRetryFrames,
                    count);
            }
        }

        private readonly struct PendingRoadInvalidation
        {
            internal readonly Entity Line;
            internal readonly string LineId;
            internal readonly string Mode;
            internal readonly LineProfile.RoadRouteSnapshot OldRoute;
            internal readonly LineProfile.RoadRouteSnapshot NewRoute;
            internal readonly uint NextRetryFrame;
            internal readonly byte RetryCount;

            internal PendingRoadInvalidation(
                Entity line,
                string lineId,
                string mode,
                LineProfile.RoadRouteSnapshot oldRoute,
                LineProfile.RoadRouteSnapshot newRoute,
                uint nextRetryFrame = 0,
                byte retryCount = 0)
            {
                Line = line;
                LineId = lineId ?? string.Empty;
                Mode = mode ?? string.Empty;
                OldRoute = oldRoute;
                NewRoute = newRoute;
                NextRetryFrame = nextRetryFrame;
                RetryCount = retryCount;
            }

            internal PendingRoadInvalidation WithLatest(LineProfile.RoadRouteSnapshot newRoute)
            {
                return new PendingRoadInvalidation(Line, LineId, Mode, OldRoute, newRoute);
            }

            internal PendingRoadInvalidation WithRetry(uint frame)
            {
                byte count = (byte)(RetryCount + 1);
                return new PendingRoadInvalidation(
                    Line,
                    LineId,
                    Mode,
                    OldRoute,
                    NewRoute,
                    frame + LayoutRetryFrames,
                    count);
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
                m_NextLayoutRetryFrame = 0;
                return;
            }

            m_Pending[line] = new PendingInvalidation(
                line,
                m_Runtime.LineStableId(line),
                TransitModeCodec.Format(TransportModeResolver.Resolve(m_Runtime.EntityManager, line)),
                oldSignature,
                newSignature,
                oldAtomCount,
                newAtomCount);
            m_NextLayoutRetryFrame = 0;
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
                m_NextLayoutRetryFrame = 0;
                return;
            }

            m_PendingRoadLines[line] = new PendingRoadInvalidation(
                line,
                m_Runtime.LineStableId(line),
                TransitModeCodec.Format(TransportModeResolver.Resolve(m_Runtime.EntityManager, line)),
                oldRoute,
                newRoute);
            m_NextLayoutRetryFrame = 0;
        }

        internal void Drain()
        {
            if (m_Pending.Count == 0 && m_PendingRoadLines.Count == 0)
                return;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_NextLayoutRetryFrame != 0 && frame < m_NextLayoutRetryFrame)
                return;
            m_NextLayoutRetryFrame = 0;

            List<PendingInvalidation> pending = new List<PendingInvalidation>(m_Pending.Values);
            m_Pending.Clear();

            List<PendingRoadInvalidation> roadLines = new List<PendingRoadInvalidation>(m_PendingRoadLines.Values);
            m_PendingRoadLines.Clear();

            bool railCachesCleared = false;
            for (int i = 0; i < pending.Count; i++)
                DrainLine(pending[i], ref railCachesCleared);
            for (int i = 0; i < roadLines.Count; i++)
                DrainRoadLine(roadLines[i]);
        }

        private void DrainRoadLine(PendingRoadInvalidation pending)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (pending.NextRetryFrame != 0 && frame < pending.NextRetryFrame)
            {
                QueueRetry(pending);
                return;
            }
            if (!m_Runtime.EntityManager.Exists(line))
            {
                ClearUnavailableRoad(pending, "line-deleted");
                return;
            }
            bool hasLayout = m_Runtime.m_LineView.TryStopLayout(
                line,
                out string stopSig,
                out int[] waypointIndices);
            if (!hasLayout || string.IsNullOrEmpty(stopSig))
            {
                string reason = LayoutTerminalReason(line, pending.RetryCount);
                if (!string.IsNullOrEmpty(reason))
                    ClearUnavailableRoad(pending, reason);
                else
                    QueueRetry(pending.WithRetry(m_Runtime.m_SimulationSystem.frameIndex));
                return;
            }
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                stopSig,
                "stop-sig-changed",
                clearDetails: false,
                publishEvent: true);
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
                    m_Runtime.m_StopRuntime.ReprojectTimedPlan(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
                    m_Runtime.m_Observation.SuppressMonitor(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
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

        private void DrainLine(PendingInvalidation pending, ref bool railCachesCleared)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (pending.NextRetryFrame != 0 && frame < pending.NextRetryFrame)
            {
                QueueRetry(pending);
                return;
            }
            if (!m_Runtime.EntityManager.Exists(line))
            {
                ClearUnavailableLine(pending, "line-deleted", ref railCachesCleared);
                return;
            }
            bool hasLayout = m_Runtime.m_LineView.TryStopLayout(
                line,
                out string stopSig,
                out int[] waypointIndices);
            if (!hasLayout || string.IsNullOrEmpty(stopSig))
            {
                string reason = LayoutTerminalReason(line, pending.RetryCount);
                if (!string.IsNullOrEmpty(reason))
                    ClearUnavailableLine(pending, reason, ref railCachesCleared);
                else
                    QueueRetry(pending.WithRetry(m_Runtime.m_SimulationSystem.frameIndex));
                return;
            }
            EnsureRailCachesCleared(ref railCachesCleared);
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                stopSig,
                "stop-sig-changed",
                clearDetails: false,
                publishEvent: true);
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

                    m_Runtime.m_StopRuntime.ReprojectTimedPlan(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
                    m_Runtime.m_Observation.SuppressMonitor(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
                    ClearVehiclePosition(vehicle, line);
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

        private void QueueRetry(PendingInvalidation pending)
        {
            m_Pending[pending.Line] = pending;
            TrackRetryFrame(pending.NextRetryFrame);
        }

        private void QueueRetry(PendingRoadInvalidation pending)
        {
            m_PendingRoadLines[pending.Line] = pending;
            TrackRetryFrame(pending.NextRetryFrame);
        }

        private void TrackRetryFrame(uint frame)
        {
            if (frame == 0)
                return;
            if (m_NextLayoutRetryFrame == 0 || frame < m_NextLayoutRetryFrame)
                m_NextLayoutRetryFrame = frame;
        }

        private string LayoutTerminalReason(Entity line, byte retryCount)
        {
            if (!m_Runtime.EntityManager.Exists(line))
                return "line-deleted";
            if (!m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return retryCount >= LayoutRetryLimit ? "route-buffer-unavailable" : string.Empty;

            DynamicBuffer<RouteWaypoint> waypoints =
                m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0)
                return "route-waypoints-empty";
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypoint)
                    && m_Runtime.m_Resolve.Stop(waypoint) != Entity.Null)
                {
                    return retryCount >= LayoutRetryLimit ? "layout-retry-exhausted" : string.Empty;
                }
            }
            return retryCount >= LayoutRetryLimit
                ? "route-has-no-valid-stop"
                : string.Empty;
        }

        private void ClearUnavailableRoad(PendingRoadInvalidation pending, string reason)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                string.Empty,
                NoticeTrigger(reason),
                clearDetails: true,
                publishEvent: false);
            ReleaseTimedPlans(line);
            m_Runtime.m_Observation.ReleaseLineMonitor(line, frame);
            RapidTransitMod.PassengerFlow.Runtime.Current?.InvalidateAnchors(line);
            m_Runtime.m_Observation.InvalidateBusRoute(line, pending.OldRoute, pending.NewRoute);
            m_Runtime.m_LineTimes.InvalidateLine(line);
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
            m_Runtime.log.Info("[LineStructureInvalidated] line=" + line.Index
                + " layout=unavailable road=1 reason=" + reason
                + " retries=" + pending.RetryCount);
            PushReleaseNotice(pending.LineId, pending.Mode, reason, pending.RetryCount);
        }

        private void ClearUnavailableLine(
            PendingInvalidation pending,
            string reason,
            ref bool railCachesCleared)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                string.Empty,
                NoticeTrigger(reason),
                clearDetails: true,
                publishEvent: false);
            ReleaseTimedPlans(line);
            m_Runtime.m_Observation.ReleaseLineMonitor(line, frame);
            EnsureRailCachesCleared(ref railCachesCleared);
            m_Runtime.m_RailEventSource.InvalidateLine(line);
            m_Runtime.m_TrackModel.InvalidateWaypointIndexLookup(line);
            m_Runtime.m_LapCache.RemoveLine(line);
            m_Runtime.m_Observation.RemoveLine(line);
            m_Runtime.m_Observation.InvalidateSliceLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);
            m_Runtime.m_Bypass.ClearLine(line);

            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        && vehicleLine == line)
                    {
                        ClearVehiclePosition(vehicle, line);
                    }
                }
            }
            finally
            {
                vehicles.Dispose();
            }
            m_Runtime.log.Info("[LineStructureInvalidated] line=" + line.Index
                + " layout=unavailable rail=1 reason=" + reason
                + " retries=" + pending.RetryCount);
            PushReleaseNotice(pending.LineId, pending.Mode, reason, pending.RetryCount);
        }

        private void ReleaseTimedPlans(Entity line)
        {
            List<Entity> vehicles = new List<Entity>();
            foreach (TimedPlanSnapshot snapshot in m_Runtime.m_StopRuntime.TimedPlans())
                if (snapshot.Line == line)
                    vehicles.Add(snapshot.Vehicle);
            for (int i = 0; i < vehicles.Count; i++)
                m_Runtime.m_StopRuntime.ClearTimedPlan(vehicles[i]);
        }

        private void PushReleaseNotice(
            string lineId,
            string mode,
            string reason,
            byte retryCount)
        {
            if (string.IsNullOrEmpty(lineId))
                return;
            DispatchWorkbenchLineInvalidationEvent payload = new DispatchWorkbenchLineInvalidationEvent
            {
                mode = mode ?? string.Empty,
                version = m_Runtime.m_WorkbenchBridge.Version.ToString(),
                lineIds = new[] { lineId },
                reasons = new[]
                {
                    new DispatchWorkbenchCleanupReasonDto
                    {
                        lineId = lineId,
                        reason = "backend-applied-cleared;default-restored;trigger="
                            + NoticeTrigger(reason)
                            + ";detail="
                            + reason
                            + ";retries="
                            + retryCount
                    }
                }
            };
            // 该通知只用于后端实际释放后的前端状态同步，避免前端继续显示已应用。
            Workbenches.UiEvents.Push(payload);
        }

        private static string NoticeTrigger(string reason)
        {
            if (string.Equals(reason, "line-deleted", System.StringComparison.Ordinal))
                return "line-deleted";
            if (string.Equals(reason, "route-waypoints-empty", System.StringComparison.Ordinal)
                || string.Equals(reason, "route-has-no-valid-stop", System.StringComparison.Ordinal))
            {
                return "no-valid-stop";
            }
            return "continuous-validation-failed";
        }

        private void EnsureRailCachesCleared(ref bool cleared)
        {
            if (cleared)
                return;
            m_Runtime.m_LineTimes.Clear();
            m_Runtime.m_LineMileage.Clear();
            m_Runtime.m_LineView.Clear();
            m_Runtime.m_TrackProjection.ClearLineRunningVehicleSnapshots();
            m_Runtime.m_StationContextQuery.Clear();
            cleared = true;
        }

        private void ClearVehiclePosition(Entity vehicle, Entity line)
        {
            m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, -1);
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_TrackProjection.ClearVehicle(vehicle);
            m_Runtime.m_ObsPersist.ClearLap(vehicle);
            m_Runtime.m_Observation.ClearVehicleSlices(vehicle);
            m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
            RuntimeStageMask stages = RuntimeStageMask.Stop;
            if (m_Runtime.EntityManager.Exists(line)
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).CanBypass)
            {
                stages |= RuntimeStageMask.Bypass;
            }
            m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, stages);
        }
    }
}
