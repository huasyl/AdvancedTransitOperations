using System;
using System.Collections.Generic;
using System.Text;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private string FormatTrackModelStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                label = "stop" + building.Index;
            }

            return label + "#" + waypointIndex;
        }

        private static string FormatTrackModelVehicleLabel(Entity vehicle, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "(" + state + ")@" + km + "km";
        }

        private string FormatTrackModelDisplayStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    label = !string.IsNullOrWhiteSpace(name)
                        ? name
                        : ("stop" + building.Index);
                }
                catch
                {
                    label = "stop" + building.Index;
                }
            }

            return label + "#" + waypointIndex;
        }

        private string FormatReadableStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    if (!string.IsNullOrWhiteSpace(name))
                        label = name;
                    else
                        label = "stop" + building.Index;
                }
                catch
                {
                    label = "stop" + building.Index;
                }
            }

            return label + "#" + waypointIndex;
        }

        private string FormatSharedMapStationLabel(Entity building)
        {
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "stop";
        }

        private string FormatSharedMapVehicleNameLabel(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(vehicle);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "vehicle";
        }

        private string ResolveTrackModelLineLabel(Entity line, bool includeEntityFallback)
        {
            if (line == Entity.Null)
                return includeEntityFallback ? "line" : string.Empty;

            try
            {
                if (m_NameSystem.TryGetCustomName(line, out string customName)
                    && !string.IsNullOrWhiteSpace(customName))
                {
                    return customName.Trim();
                }
            }
            catch
            {
            }

            if (EntityManager.Exists(line)
                && EntityManager.HasComponent<RouteNumber>(line))
            {
                RouteNumber routeNumber = EntityManager.GetComponentData<RouteNumber>(line);
                if (routeNumber.m_Number > 0)
                    return "line" + routeNumber.m_Number;
            }

            try
            {
                string rendered = m_NameSystem.GetRenderedLabelName(line);
                if (!string.IsNullOrWhiteSpace(rendered)
                    && !rendered.Contains("Tool")
                    && !rendered.Contains("Tool")
                    && !rendered.Contains("Route Tool")
                    && !rendered.Contains("Route Tool"))
                {
                    return rendered.Trim();
                }
            }
            catch
            {
            }

            return includeEntityFallback ? ("line" + line.Index) : "line";
        }

        private string FormatSharedMapLineLabel(Entity line)
        {
            return ResolveTrackModelLineLabel(line, includeEntityFallback: false);
        }

        private string FormatSharedMapVehicleLabel(Entity vehicle, Entity line, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return FormatSharedMapVehicleNameLabel(vehicle) + "[" + FormatSharedMapLineLabel(line) + "](" + state + ")@" + km + "km";
        }

        private string FormatSharedMapUnknownVehicleLabel(Entity vehicle, Entity line, string state)
        {
            return FormatSharedMapVehicleNameLabel(vehicle) + "[" + FormatSharedMapLineLabel(line) + "](" + state + ")@?";
        }

        private string FormatReadableVehicleLabel(Entity vehicle, Entity line, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "[" + FormatReadableLineLabel(line) + "](" + state + ")@" + km + "km";
        }

        private string FormatReadableUnknownVehicleLabel(Entity vehicle, Entity line, string state)
        {
            return "vehicle" + vehicle.Index + "[" + FormatReadableLineLabel(line) + "](" + state + ")@?";
        }

        private string FormatReadableLineLabel(Entity line)
        {
            if (line == Entity.Null)
                return "-";

            return ResolveTrackModelLineLabel(line, includeEntityFallback: true);
        }

        private static string FormatTrackModelDisplayVehicleLabel(Entity vehicle, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "(" + state + ")@" + km + "km";
        }

        private string DescribeTraversalInputs(DynamicBuffer<PathElement> pathElements, int pathIndex)
        {
            if (pathIndex < 0 || pathIndex >= pathElements.Length)
                return string.Empty;

            PathElement current = pathElements[pathIndex];
            Entity previousTarget = pathIndex > 0 ? pathElements[pathIndex - 1].m_Target : Entity.Null;
            Entity nextTarget = pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target : Entity.Null;
            bool reverseFlag = (current.m_Flags & PathElementFlags.Reverse) != 0;

            StringBuilder sb = new StringBuilder();
            sb.Append(" prev=").Append(previousTarget == Entity.Null ? "null" : previousTarget.Index.ToString())
              .Append(" curr=").Append(current.m_Target == Entity.Null ? "null" : current.m_Target.Index.ToString())
              .Append(" next=").Append(nextTarget == Entity.Null ? "null" : nextTarget.Index.ToString())
              .Append(" reverseFlag=").Append(reverseFlag ? "1" : "0");

            if (EntityManager.HasComponent<TrackLane>(current.m_Target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                bool invert = (trackLane.m_Flags & TrackLaneFlags.Invert) != 0;
                sb.Append(" invert=").Append(invert ? "1" : "0");
            }

            if (EntityManager.HasComponent<EdgeLane>(current.m_Target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(current.m_Target);
                bool edgeForward = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                sb.Append(" edgeForward=").Append(edgeForward ? "1" : "0")
                  .Append(" edgeDelta=(")
                  .Append(edgeLane.m_EdgeDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(edgeLane.m_EdgeDelta.y.ToString("F2"))
                  .Append(")");
            }

            float laneProgress = current.m_TargetDelta.y - current.m_TargetDelta.x;
            sb.Append(" laneProgress=").Append(laneProgress.ToString("F2"));
            return sb.ToString();
        }

        private string DescribePathElementTarget(Entity target)
        {
            if (target == Entity.Null || !EntityManager.Exists(target))
                return "null";

            StringBuilder sb = new StringBuilder();
            sb.Append("target=").Append(target.Index);

            if (EntityManager.HasComponent<TrackLane>(target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(target);
                sb.Append(" TrackLane")
                  .Append(" flags=").Append(trackLane.m_Flags)
                  .Append(" speed=").Append(trackLane.m_SpeedLimit.ToString("F1"));
            }

            if (EntityManager.HasComponent<Lane>(target))
                sb.Append(" Lane");

            if (EntityManager.HasComponent<EdgeLane>(target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(target);
                sb.Append(" EdgeLane")
                  .Append(" edgeDelta=(")
                  .Append(edgeLane.m_EdgeDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(edgeLane.m_EdgeDelta.y.ToString("F2"))
                  .Append(")");
            }

            if (EntityManager.HasComponent<ConnectionLane>(target))
            {
                ConnectionLane connectionLane = EntityManager.GetComponentData<ConnectionLane>(target);
                sb.Append(" ConnectionLane")
                  .Append(" trackTypes=").Append(connectionLane.m_TrackTypes)
                  .Append(" flags=").Append(connectionLane.m_Flags);
            }

            if (EntityManager.HasComponent<TrainTrack>(target))
                sb.Append(" TrainTrack");
            if (EntityManager.HasComponent<TramTrack>(target))
                sb.Append(" TramTrack");
            if (EntityManager.HasComponent<SubwayTrack>(target))
                sb.Append(" SubwayTrack");

            return sb.ToString();
        }

        private void LogRouteSegmentPathElementDiagnostics(Entity line, int waypointIndex, Entity segmentEntity)
        {
            if (segmentEntity == Entity.Null || !EntityManager.Exists(segmentEntity))
            {
                log.Info("[TrackModelRaw] line=" + line.Index + " wp=" + waypointIndex + " segment=null");
                return;
            }

            if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
            {
                log.Info("[TrackModelRaw] line=" + line.Index + " wp=" + waypointIndex + " segment=" + segmentEntity.Index + " pathElements=none");
                return;
            }

            DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModelRaw] line=").Append(line.Index)
              .Append(" wp=").Append(waypointIndex)
              .Append(" segment=").Append(segmentEntity.Index)
              .Append(" pathCount=").Append(pathElements.Length);

            int limit = math.min(pathElements.Length, 16);
            for (int pathIndex = 0; pathIndex < limit; pathIndex++)
            {
                PathElement element = pathElements[pathIndex];
                TrackAtomClass atomClass = ClassifyPathElementTarget(element);
                TrackTraversalDir traversalDir = ResolveTraversalDirection(pathElements, pathIndex);
                sb.Append(" | ")
                  .Append(pathIndex)
                  .Append(":")
                  .Append(DescribePathElementTarget(element.m_Target))
                  .Append(" flags=").Append(element.m_Flags)
                  .Append(" delta=(")
                  .Append(element.m_TargetDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(element.m_TargetDelta.y.ToString("F2"))
                  .Append(")")
                  .Append(" class=").Append(atomClass)
                  .Append(" dir=").Append(traversalDir)
                  .Append(" token=").Append(pathIndex > 0 ? pathElements[pathIndex - 1].m_Target.Index.ToString() : "null")
                  .Append("->").Append(element.m_Target.Index)
                  .Append("->").Append(pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target.Index.ToString() : "null")
                  .Append(DescribeTraversalInputs(pathElements, pathIndex));
            }

            log.Info(sb.ToString());
        }

        private void LogSharedTrackIndexSummary()
        {
            EnsureSharedTrackIndexCurrent();

            int sharedAtomKeys = 0;
            int sharedOccurrences = 0;
            var contextCountByPhysicalTarget = new Dictionary<Entity, int>();
            var adjacencyByPhysicalTarget = new Dictionary<Entity, HashSet<string>>();
            foreach (KeyValuePair<TrackAtomKey, List<SharedTrackOccurrence>> entry in m_SharedTrackIndex)
            {
                if (!contextCountByPhysicalTarget.TryGetValue(entry.Key.PhysicalLaneKey, out int contextCount))
                    contextCount = 0;

                contextCountByPhysicalTarget[entry.Key.PhysicalLaneKey] = contextCount + 1;

                if (!adjacencyByPhysicalTarget.TryGetValue(entry.Key.PhysicalLaneKey, out HashSet<string> adjacencySet))
                {
                    adjacencySet = new HashSet<string>(StringComparer.Ordinal);
                    adjacencyByPhysicalTarget[entry.Key.PhysicalLaneKey] = adjacencySet;
                }

                string previous = entry.Key.PreviousTarget == Entity.Null ? "null" : entry.Key.PreviousTarget.Index.ToString();
                string next = entry.Key.NextTarget == Entity.Null ? "null" : entry.Key.NextTarget.Index.ToString();
                adjacencySet.Add(previous + ">" + next);

                if (entry.Value == null || entry.Value.Count <= 1)
                    continue;

                sharedAtomKeys++;
                sharedOccurrences += entry.Value.Count;
            }

            int contextSplitTargets = 0;
            int mirroredTargets = 0;
            foreach (KeyValuePair<Entity, int> entry in contextCountByPhysicalTarget)
            {
                if (entry.Value > 1)
                    contextSplitTargets++;
            }

            foreach (KeyValuePair<Entity, HashSet<string>> entry in adjacencyByPhysicalTarget)
            {
                bool hasMirror = false;
                foreach (string pair in entry.Value)
                {
                    int separator = pair.IndexOf('>');
                    if (separator < 0)
                        continue;

                    string previous = pair.Substring(0, separator);
                    string next = pair.Substring(separator + 1);
                    if (entry.Value.Contains(next + ">" + previous))
                    {
                        hasMirror = true;
                        break;
                    }
                }

                if (hasMirror)
                    mirroredTargets++;
            }

            log.Info("[TrackModelShared] keys=" + m_SharedTrackIndex.Count
                + " physicalTargets=" + contextCountByPhysicalTarget.Count
                + " sharedKeys=" + sharedAtomKeys
                + " sharedOccurrences=" + sharedOccurrences
                + " contextSplitTargets=" + contextSplitTargets
                + " mirroredTargets=" + mirroredTargets);
        }

        private void LogLineTrackChainDiagnostics(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (EntityManager.HasBuffer<RouteSegment>(line))
            {
                DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
                int rawLimit = math.min(segments.Length, 4);
                for (int waypointIndex = 0; waypointIndex < rawLimit; waypointIndex++)
                    LogRouteSegmentPathElementDiagnostics(line, waypointIndex, segments[waypointIndex].m_Segment);
            }

            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                log.Info("[TrackModel] line=" + line.Index + " chain=unavailable");
                return;
            }

            EnsureTrackChainBypassPipelineReady(chain);

            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModel] line=").Append(line.Index)
              .Append(" atoms=").Append(chain.TrackAtoms.Count)
              .Append(" controlPoints=").Append(chain.ControlPoints.Count)
              .Append(" controlEdges=").Append(chain.ControlEdges.Count)
              .Append(" sharedRuns=").Append(chain.SharedRuns.Count)
              .Append(" edgeSharedSpans=").Append(chain.ControlEdgeSharedSpans.Count)
              .Append(" protectedIntervals=").Append(chain.BypassProtectedIntervals.Count)
              .Append(" protectedShared=").Append(chain.ProtectedSharedIntervals.Count)
              .Append(" signature=").Append(chain.Signature)
              .Append(" firstAtoms=");

            int limit = math.min(chain.TrackAtoms.Count, 12);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");

                TrackAtom atom = chain.TrackAtoms[i];
                sb.Append(atom.Key.PhysicalLaneKey.Index)
                  .Append(":")
                  .Append(atom.Key.PreviousTarget == Entity.Null ? "null" : atom.Key.PreviousTarget.Index.ToString())
                  .Append(">")
                  .Append(atom.Key.NextTarget == Entity.Null ? "null" : atom.Key.NextTarget.Index.ToString())
                  .Append(":")
                  .Append(atom.AtomClass);
            }

            log.Info(sb.ToString());

            if (chain.SharedRuns.Count > 0)
            {
                StringBuilder runSb = new StringBuilder();
                runSb.Append("[TrackModelRuns] line=").Append(line.Index);
                int runLimit = math.min(chain.SharedRuns.Count, 8);
                for (int i = 0; i < runLimit; i++)
                {
                    SharedTrackRun run = chain.SharedRuns[i];
                    runSb.Append(" | run").Append(i)
                      .Append("=").Append(run.StartAtomIndex)
                      .Append("..").Append(run.EndAtomIndexExclusive)
                      .Append(" sharedLines=").Append(run.SharedLineCount)
                      .Append(" mirrored=").Append(run.HasMirroredContext ? "1" : "0");
                }

                log.Info(runSb.ToString());
            }

            if (chain.ControlEdgeSharedSpans.Count > 0)
            {
                StringBuilder edgeSb = new StringBuilder();
                edgeSb.Append("[TrackModelEdges] line=").Append(line.Index);
                int edgeLimit = math.min(chain.ControlEdgeSharedSpans.Count, 8);
                for (int i = 0; i < edgeLimit; i++)
                {
                    ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[i];
                    edgeSb.Append(" | edge").Append(span.ControlEdgeIndex)
                        .Append("=").Append(span.StartAtomIndex)
                        .Append("..").Append(span.EndAtomIndexExclusive)
                        .Append(" sharedLines=").Append(span.SharedLineCount)
                        .Append(" mirrored=").Append(span.HasMirroredContext ? "1" : "0");
                }

                log.Info(edgeSb.ToString());
            }

            if (chain.BypassProtectedIntervals.Count > 0)
            {
                StringBuilder protectedSb = new StringBuilder();
                protectedSb.Append("[TrackModelProtected] line=").Append(line.Index);
                int protectedLimit = math.min(chain.BypassProtectedIntervals.Count, 6);
                for (int i = 0; i < protectedLimit; i++)
                {
                    BypassProtectedInterval interval = chain.BypassProtectedIntervals[i];
                    protectedSb.Append(" | p").Append(i)
                        .Append(" cp=").Append(interval.StartControlPointIndex).Append("->").Append(interval.EndControlPointIndex)
                        .Append(" edges=").Append(interval.StartControlEdgeIndex).Append("..").Append(interval.EndControlEdgeIndexInclusive)
                        .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                        .Append(" baseFrames=").Append(interval.BaseFrames.ToString("F1"));
                }

                log.Info(protectedSb.ToString());
            }

            if (chain.ProtectedSharedIntervals.Count > 0)
            {
                StringBuilder overlapSb = new StringBuilder();
                overlapSb.Append("[TrackModelProtectedShared] line=").Append(line.Index);
                int overlapLimit = math.min(chain.ProtectedSharedIntervals.Count, 8);
                for (int i = 0; i < overlapLimit; i++)
                {
                    ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                    overlapSb.Append(" | ps").Append(i)
                        .Append(" p=").Append(interval.ProtectedIntervalIndex)
                        .Append(" edge=").Append(interval.ControlEdgeIndex)
                        .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                        .Append(" sharedLines=").Append(interval.SharedLineCount)
                        .Append(" mirrored=").Append(interval.HasMirroredContext ? "1" : "0")
                        .Append(" entry=").Append(interval.EntryOffsetFrames.ToString("F1"))
                        .Append(" clear=").Append(interval.ClearOffsetFrames.ToString("F1"));
                }

                log.Info(overlapSb.ToString());
            }

            if (chain.TurnbackBoundaries.Count > 0)
            {
                StringBuilder turnbackSb = new StringBuilder();
                turnbackSb.Append("[TrackModelTurnback] line=").Append(line.Index);
                int turnbackLimit = math.min(chain.TurnbackBoundaries.Count, 8);
                for (int i = 0; i < turnbackLimit; i++)
                {
                    TurnbackBoundary boundary = chain.TurnbackBoundaries[i];
                    turnbackSb.Append(" | tb").Append(i).Append("=")
                        .Append(FormatTurnbackBoundaryLabel(chain, boundary));
                }

                log.Info(turnbackSb.ToString());
            }

            if (chain.BypassProtectedIntervals.Count > 0)
            {
                StringBuilder summarySb = new StringBuilder();
                summarySb.Append("[TrackModelProtectedSummary] line=").Append(line.Index);
                int summaryLimit = math.min(chain.ProtectedIntervalSummaries.Count, 6);
                for (int i = 0; i < summaryLimit; i++)
                {
                    ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[i];
                    summarySb.Append(" | p").Append(i)
                        .Append(" sharedSegments=").Append(summary.SharedSegmentCount)
                        .Append(" maxSharedLines=").Append(summary.MaxSharedLineCount)
                        .Append(" mirrored=").Append(summary.HasMirroredContext ? "1" : "0")
                        .Append(" minEntry=").Append(summary.MinEntryOffsetFrames.ToString("F1"))
                        .Append(" maxClear=").Append(summary.MaxClearOffsetFrames.ToString("F1"));
                }

                log.Info(summarySb.ToString());
            }

            LogSharedTrackIndexSummary();
        }

        private void LogSharedWindowFinalReject(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity expressVehicle,
            string rejectReason)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || string.IsNullOrWhiteSpace(rejectReason))
                return;

            string state = "finalReject|" + rejectReason;
            var pairKey = new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, expressVehicle);
            if (m_SharedWindowAuditPairStateCache.TryGetValue(pairKey, out string previousState)
                && previousState == state)
            {
                return;
            }
            m_SharedWindowAuditPairStateCache[pairKey] = state;
        }

        private void TryLogSharedWindowAuditForNoExpress(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || localChain == null)
            {
                return;
            }

            if (!IsTrackModelDiagnosticLoggingEnabled())
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = "no-express-in-shared-window|"
                + localLine.Index
                + "|"
                + protectedIntervalIndex
                + "|"
                + currentBypassBuilding.Index;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_SharedWindowAuditThrottleCache,
                    m_SharedWindowAuditLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            string auditSummary = BuildSharedWindowAuditSummary(
                localVehicle,
                localLine,
                localWaypoints,
                localChain,
                currentWaypointIndex,
                currentBypassBuilding,
                protectedInterval);
            LogSharedWindowAuditOnce(
                localVehicle,
                localLine,
                protectedIntervalIndex,
                auditSummary);
        }

        private bool TryGetSharedWindowPairState(
            Entity localVehicle,
            int protectedIntervalIndex,
            Entity expressVehicle,
            out string state)
        {
            state = string.Empty;
            if (localVehicle == Entity.Null || expressVehicle == Entity.Null)
                return false;

            return m_SharedWindowAuditPairStateCache.TryGetValue(
                new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, expressVehicle),
                out state)
                && !string.IsNullOrWhiteSpace(state);
        }

        private bool TryBuildTrackModelSequenceSummary(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out string summary)
        {
            summary = string.Empty;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasBuffer<RouteVehicle>(line))
            {
                return false;
            }

            if (!TryGetLineMileageModel(line, waypoints, out LineMileageModel model)
                || model.TotalDistanceMeters <= 0f
                || model.WaypointDistances.Length != waypoints.Length)
            {
                return false;
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(waypoints.Length + routeVehicles.Length);
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
                items.Add(new TrackModelSequenceItem(
                    model.WaypointDistances[waypointIndex],
                    0,
                    FormatReadableStationLabel(building, waypointIndex)));
            }

            for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
            {
                Entity vehicle = routeVehicles[rvIndex].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    continue;

                string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                    ? vehicleState.ToString()
                    : "Unknown";

                if (TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection projection))
                {
                    items.Add(new TrackModelSequenceItem(
                        projection.DistanceMeters,
                        1,
                        FormatReadableVehicleLabel(vehicle, line, state, projection.DistanceMeters)));
                }
                else
                {
                    items.Add(new TrackModelSequenceItem(
                        float.MaxValue,
                        1,
                        "vehicle" + vehicle.Index + "(" + state + ")@?"));
                }
            }

            if (items.Count == 0)
                return false;

            items.Sort((a, b) =>
            {
                int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                if (cmp != 0)
                    return cmp;

                cmp = a.KindOrder.CompareTo(b.KindOrder);
                if (cmp != 0)
                    return cmp;

                return string.CompareOrdinal(a.Label, b.Label);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("seq=");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");
                sb.Append(items[i].Label);
            }

            summary = sb.ToString();
            return true;
        }

        private bool TryBuildBypassDecisionSequenceSummary(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            BypassProtectedInterval localProtectedInterval,
            out string summary)
        {
            summary = string.Empty;
            if (localLine == Entity.Null
                || !EntityManager.Exists(localLine)
                || !TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return false;
            }

            Entity currentBypassBuilding = GetBypassBuildingForWaypoint(localWaypoints, currentWaypointIndex);
            List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(localWaypoints.Length + 8);
            for (int controlPointIndex = localProtectedInterval.StartControlPointIndex; controlPointIndex <= localProtectedInterval.EndControlPointIndex; controlPointIndex++)
            {
                ControlPointMarker marker = localChain.ControlPoints[controlPointIndex];
                items.Add(new TrackModelSequenceItem(
                    MapControlPointToProtectedIntervalCoordinate(localChain, localProtectedInterval, controlPointIndex),
                    0,
                    FormatSharedMapStationLabel(marker.Building)));
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (routeVehicleBuffers.TryGetBuffer(localLine, out DynamicBuffer<RouteVehicle> localVehicles))
            {
                for (int rvIndex = 0; rvIndex < localVehicles.Length; rvIndex++)
                {
                    Entity vehicle = localVehicles[rvIndex].m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                        continue;

                    string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                        ? vehicleState.ToString()
                        : "Unknown";

                    if (!TryProjectTrackModelRuntimePosition(vehicle, localLine, localWaypoints, localProtectedInterval, out TrackModelRuntimePosition runtimePosition))
                        continue;
                    if (runtimePosition.Confidence < 0.6f)
                        continue;

                    float coordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(runtimePosition, localProtectedInterval, includeApproachers: true, out bool include);
                    if (!include)
                        continue;

                    items.Add(new TrackModelSequenceItem(
                        coordinate,
                        1,
                        FormatSharedMapVehicleLabel(vehicle, localLine, state, coordinate)));
                }
            }

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !routeVehicleBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteVehicle> expressVehicles)
                    || !TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                {
                    continue;
                }

                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, localProtectedInterval, currentBypassBuilding, expressChain);
                if (!sharedWindowMatch.Found || sharedWindowMatch.Ambiguous)
                    continue;
                for (int rvIndex = 0; rvIndex < expressVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = expressVehicles[rvIndex].m_Vehicle;
                    if (expressVehicle == Entity.Null || expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                        continue;

                    string state = m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState)
                        ? expressState.ToString()
                        : "Unknown";

                    if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, sharedWindowMatch.ExpressSharedWindow, out TrackModelRuntimePosition expressPosition))
                        continue;
                    if (expressPosition.Confidence < 0.6f)
                        continue;

                    float mappedMeters = MapRuntimePositionToReferenceWindowCoordinate(
                        expressPosition,
                        sharedWindowMatch.ExpressSharedWindow,
                        sharedWindowMatch.LocalSharedWindow,
                        localProtectedInterval,
                        includeApproachers: true,
                        out bool include);
                    if (!include)
                        continue;

                    items.Add(new TrackModelSequenceItem(
                        mappedMeters,
                        1,
                        FormatSharedMapVehicleLabel(expressVehicle, expressLine, state, mappedMeters)));
                }
            }

            if (items.Count == 0)
                return false;

            items.Sort((a, b) =>
            {
                int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                if (cmp != 0)
                    return cmp;

                cmp = a.KindOrder.CompareTo(b.KindOrder);
                if (cmp != 0)
                    return cmp;

                return string.CompareOrdinal(a.Label, b.Label);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("seq=");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");
                sb.Append(items[i].Label);
            }

            summary = sb.ToString();
            return true;
        }

        public void RequestDumpTrackModelSnapshot()
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                LogIndependentSharedPhysicalCorridorDump(lines);
                LogTrackModelReplayDump(lines);
            }
            finally
            {
                lines.Dispose();
            }
        }

        private static float MapExpressPositionToLocalProtectedInterval(
            TrackModelRuntimePosition expressPosition,
            BypassProtectedInterval expressProtectedInterval,
            float localStartMeters,
            float localEndMeters)
        {
            const float epsilonMeters = 1f;
            switch (expressPosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    return math.max(0f, localStartMeters - epsilonMeters);
                case TrackModelRelativeToProtectedInterval.After:
                    return localEndMeters + epsilonMeters;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    int atomSpan = math.max(1, expressProtectedInterval.EndAtomIndexExclusive - expressProtectedInterval.StartAtomIndex);
                    float progress01 = math.saturate((expressPosition.CurrentAtomIndex - expressProtectedInterval.StartAtomIndex) / (float)atomSpan);
                    return math.lerp(localStartMeters, localEndMeters, progress01);
                }
                default:
                    return localEndMeters + epsilonMeters * 2f;
            }
        }

        private static float ResolveWaypointDistanceForControlPoint(LineMileageModel model, LineTrackChain chain, int controlPointIndex)
        {
            if (model == null
                || chain == null
                || controlPointIndex < 0
                || controlPointIndex >= chain.ControlPoints.Count)
            {
                return 0f;
            }

            int waypointIndex = chain.ControlPoints[controlPointIndex].WaypointIndex;
            if (waypointIndex < 0 || waypointIndex >= model.WaypointDistances.Length)
                return 0f;

            return model.WaypointDistances[waypointIndex];
        }

        private string BuildBypassDecisionSequenceSummaryForLogging(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            int protectedIntervalIndex)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || !TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return string.Empty;
            }

            EnsureTrackChainBypassPipelineReady(localChain);
            if (protectedIntervalIndex < 0 || protectedIntervalIndex >= localChain.BypassProtectedIntervals.Count)
                return string.Empty;

            return TryBuildBypassDecisionSequenceSummary(
                localVehicle,
                localLine,
                localWaypoints,
                currentWaypointIndex,
                localChain.BypassProtectedIntervals[protectedIntervalIndex],
                out string sequenceSummary)
                ? sequenceSummary
                : string.Empty;
        }

        private string FormatTurnbackBoundaryLabel(LineTrackChain chain, TurnbackBoundary boundary)
        {
            string boundaryLabel = "atom" + boundary.AtomIndex;
            if (chain != null
                && chain.TraversalProfile != null
                && boundary.BoundaryEventIndex >= 0
                && boundary.BoundaryEventIndex < chain.TraversalProfile.Events.Count)
            {
                TraversalEvent boundaryEvent = chain.TraversalProfile.Events[boundary.BoundaryEventIndex];
                if (boundaryEvent.Building != Entity.Null)
                    boundaryLabel = FormatBypassNodeLabel(boundaryEvent.Building) + "@" + boundary.AtomIndex;
            }

            return boundaryLabel
                + " slices=" + boundary.BeforeSliceIndex + "->" + boundary.AfterSliceIndex
                + (boundary.IsLearned
                    ? " learnedHits=" + boundary.MatchedAtomCount
                    : " match=" + boundary.MatchedAtomCount + " unique=" + boundary.MatchedUniqueLaneCount);
        }

        private void TryLogTrainLaneSourceDisagreement(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            string stage)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Game.Vehicles.TrainCurrentLane>(vehicle))
            {
                return;
            }

            if (!IsTrackModelDiagnosticLoggingEnabled())
                return;

            Entity pathTarget = Entity.Null;
            int pathElementIndex = -1;
            if (EntityManager.HasComponent<Game.Pathfind.PathOwner>(vehicle)
                && EntityManager.HasBuffer<Game.Pathfind.PathElement>(vehicle))
            {
                Game.Pathfind.PathOwner pathOwner = EntityManager.GetComponentData<Game.Pathfind.PathOwner>(vehicle);
                DynamicBuffer<Game.Pathfind.PathElement> path = EntityManager.GetBuffer<Game.Pathfind.PathElement>(vehicle, true);
                if (pathOwner.m_ElementIndex >= 0 && pathOwner.m_ElementIndex < path.Length)
                {
                    pathElementIndex = pathOwner.m_ElementIndex;
                    pathTarget = path[pathElementIndex].m_Target;
                }
            }

            Game.Vehicles.TrainCurrentLane currentLane = EntityManager.GetComponentData<Game.Vehicles.TrainCurrentLane>(vehicle);
            Entity frontLane = currentLane.m_Front.m_Lane;
            Entity rearLane = currentLane.m_Rear.m_Lane;
            Entity navigationLane = Entity.Null;
            if (EntityManager.HasBuffer<Game.Vehicles.TrainNavigationLane>(vehicle))
            {
                DynamicBuffer<Game.Vehicles.TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<Game.Vehicles.TrainNavigationLane>(vehicle, true);
                if (navigationLanes.Length > 0)
                    navigationLane = navigationLanes[navigationLanes.Length - 1].m_Lane;
            }

            Entity cursorLane = Entity.Null;
            if (cursor.AtomCursorIndex >= 0 && cursor.AtomCursorIndex < chain.TrackAtoms.Count)
                cursorLane = chain.TrackAtoms[cursor.AtomCursorIndex].Key.PhysicalLaneKey;

            int distinctLaneCount = 0;
            HashSet<Entity> uniqueLanes = new HashSet<Entity>();
            if (pathTarget != Entity.Null && uniqueLanes.Add(pathTarget))
                distinctLaneCount++;
            if (frontLane != Entity.Null && uniqueLanes.Add(frontLane))
                distinctLaneCount++;
            if (rearLane != Entity.Null && uniqueLanes.Add(rearLane))
                distinctLaneCount++;
            if (navigationLane != Entity.Null && uniqueLanes.Add(navigationLane))
                distinctLaneCount++;
            if (cursorLane != Entity.Null && uniqueLanes.Add(cursorLane))
                distinctLaneCount++;

            if (distinctLaneCount <= 1)
                return;

            string key = stage
                + "|line=" + line.Index
                + "|pathIdx=" + pathElementIndex
                + "|path=" + pathTarget.Index
                + "|front=" + frontLane.Index
                + "|rear=" + rearLane.Index
                + "|nav=" + navigationLane.Index
                + "|cursorLane=" + cursorLane.Index
                + "|cursorAtom=" + cursor.AtomCursorIndex
                + "|seg=" + cursor.SegmentIndex;

            string message = "[TrainLaneSource] vehicle=" + vehicle.Index
                + " line=" + line.Index
                + " stage=" + stage
                + " pathIdx=" + pathElementIndex
                + " pathTarget=" + pathTarget.Index
                + " frontLane=" + frontLane.Index
                + " rearLane=" + rearLane.Index
                + " navLast=" + navigationLane.Index
                + " cursorLane=" + cursorLane.Index
                + " cursorAtom=" + cursor.AtomCursorIndex
                + " seg=" + cursor.SegmentIndex
                + " conf=" + cursor.Confidence.ToString("0.00");

            LogVehicleStateOnce(m_TrainLaneSourceDiagnosticLogCache, vehicle, key, message);
        }

        private void LogBypassSelectedBlockerDetailOnce(
            Entity localVehicle,
            Entity localLine,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            Entity expressVehicle,
            Entity expressLine,
            int expressProtectedIntervalIndex,
            string intervalResolutionSource,
            int overlapCount,
            int orderedRun,
            int expressCurrentAtomIndex,
            int expressPhaseEndAtomExclusive,
            string expressPositionText)
        {
            if (!IsTrackModelDiagnosticLoggingEnabled())
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = localProtectedIntervalIndex.ToString()
                + "|b=" + currentBypassBuilding.Index
                + "|x=" + expressVehicle.Index
                + "|xp=" + expressProtectedIntervalIndex
                + "|src=" + intervalResolutionSource
                + "|ov=" + overlapCount
                + "|run=" + orderedRun;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_BypassSelectedBlockerDetailLogCache,
                    m_BypassSelectedBlockerDetailLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            string key = coarseKey
                + "|ov=" + overlapCount
                + "|run=" + orderedRun
                + "|atom=" + expressCurrentAtomIndex
                + "|phaseEnd=" + expressPhaseEndAtomExclusive
                + "|pos=" + expressPositionText;

            string lineTag = localLine != Entity.Null ? ("line" + localLine.Index) : "line";
            string expressLineTag = expressLine != Entity.Null ? ("line" + expressLine.Index) : "line";
            string message = "[selected-blocker] " + lineTag + " local=" + localVehicle.Index
                + " localInterval=" + localProtectedIntervalIndex
                + " current=" + currentBypassBuilding.Index
                + " blocker=" + expressVehicle.Index
                + " expressLine=" + expressLineTag
                + " expressInterval=" + expressProtectedIntervalIndex
                + " src=" + intervalResolutionSource
                + " overlap=" + overlapCount
                + " run=" + orderedRun
                + " expressAtom=" + expressCurrentAtomIndex
                + (expressPhaseEndAtomExclusive >= 0 ? " phaseEnd=" + expressPhaseEndAtomExclusive : string.Empty)
                + " " + expressPositionText;

            log.Info(message);
        }

        private void LogSameStationMissDiagnosticOnce(
            Entity localVehicle,
            Entity localLine,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            Entity expressVehicle,
            int candidateCount,
            string reason)
        {
            if (!IsTrackModelDiagnosticLoggingEnabled()
                || localVehicle == Entity.Null
                || expressVehicle == Entity.Null
                || string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            string key = "p=" + localProtectedIntervalIndex
                + "|b=" + currentBypassBuilding.Index
                + "|c=" + candidateCount
                + "|e=" + expressVehicle.Index
                + "|r=" + reason;
            string lineTag = localLine != Entity.Null ? ("line" + localLine.Index) : "line";
            string message = "[same-station-miss] " + lineTag
                + " local=" + localVehicle.Index
                + " protectedInterval=" + localProtectedIntervalIndex
                + " current=" + currentBypassBuilding.Index
                + " candidates=" + candidateCount
                + " express=" + expressVehicle.Index
                + " reason=" + reason;
            LogVehicleStateOnce(m_SameStationMissLogCache, localVehicle, key, message);
        }

        private bool TryBuildBypassTrackModelSummary(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out string summary)
        {
            summary = string.Empty;
            if (!TryEvaluateBypassTrackModelSummary(line, waypoints, currentWaypointIndex, out _, out _, out summary))
                return false;
            return true;
        }

        private void LogSharedWindowAuditOnce(
            Entity localVehicle,
            Entity localLine,
            int protectedIntervalIndex,
            string auditSummary)
        {
            if (!IsTrackModelDiagnosticLoggingEnabled())
                return;

            if (localVehicle == Entity.Null || string.IsNullOrWhiteSpace(auditSummary))
                return;

            string key = localLine.Index + "|" + protectedIntervalIndex + "|" + auditSummary;
            if (m_SharedWindowAuditSummaryLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_SharedWindowAuditSummaryLogCache[localVehicle] = key;
            log.Info("[SharedWindowAudit] localVehicle=" + localVehicle.Index
                + " localLine=" + localLine.Index
                + " protectedInterval=" + protectedIntervalIndex
                + " " + auditSummary);
        }

        private string BuildSharedWindowNoMatchDebugSummary(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return string.Empty;

            StringBuilder debug = new StringBuilder();
            debug.Append(" localP=").Append(localProtectedInterval.StartAtomIndex)
                .Append("..").Append(localProtectedInterval.EndAtomIndexExclusive);

            if (TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex))
            {
                debug.Append(" exit=").Append(stationExitAtomIndex)
                    .Append(" gate<=").Append(stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS);
            }
            else
            {
                debug.Append(" exit=none");
            }

            if (localChain.SharedRunsByOtherLine.TryGetValue(expressChain.LineEntity, out List<SharedTrackRun> localSharedRuns)
                && localSharedRuns != null
                && localSharedRuns.Count > 0)
            {
                debug.Append(" localRuns=");
                int written = 0;
                for (int i = 0; i < localSharedRuns.Count && written < 4; i++)
                {
                    SharedTrackRun run = localSharedRuns[i];
                    int start = math.max(run.StartAtomIndex, localProtectedInterval.StartAtomIndex);
                    int endExclusive = math.min(run.EndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                    if (endExclusive <= start)
                        continue;

                    if (written > 0)
                        debug.Append(",");

                    debug.Append(start).Append("..").Append(endExclusive);
                    written++;
                }

                if (written == 0)
                    debug.Append("none-in-local");
            }
            else
            {
                debug.Append(" localRuns=none");
            }

            if (expressChain.SharedRunsByOtherLine.TryGetValue(localChain.LineEntity, out List<SharedTrackRun> expressSharedRuns)
                && expressSharedRuns != null
                && expressSharedRuns.Count > 0)
            {
                debug.Append(" expressRuns=");
                int written = 0;
                for (int i = 0; i < expressSharedRuns.Count && written < 4; i++)
                {
                    SharedTrackRun run = expressSharedRuns[i];
                    if (written > 0)
                        debug.Append(",");

                    debug.Append(run.StartAtomIndex).Append("..").Append(run.EndAtomIndexExclusive);
                    written++;
                }
            }
            else
            {
                debug.Append(" expressRuns=none");
            }

            GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
            if (snapshot == null || snapshot.Segments.Count == 0)
            {
                debug.Append(" pairSegs=none");
                return debug.Append(BuildSharedWindowNoMatchPairAttemptSummary(
                    localChain,
                    localProtectedInterval,
                    expressChain,
                    localSharedRuns,
                    expressSharedRuns)).ToString();
            }

            debug.Append(" pairSegs=");
            int segmentWritten = 0;
            for (int i = 0; i < snapshot.Segments.Count && segmentWritten < 4; i++)
            {
                GlobalSharedTrunkSegment segment = snapshot.Segments[i];
                int localStart = math.max(segment.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
                int localEndExclusive = math.min(segment.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                if (localEndExclusive <= localStart)
                    continue;

                if (segmentWritten > 0)
                    debug.Append(",");

                debug.Append("L").Append(localStart).Append("..").Append(localEndExclusive)
                    .Append("/E").Append(segment.ExpressCorridorStartAtomIndex).Append("..").Append(segment.ExpressCorridorEndAtomIndexExclusive)
                    .Append("/")
                    .Append(segment.TraversalRelation == SharedTraversalRelation.SameDirection
                        ? "same"
                        : segment.TraversalRelation == SharedTraversalRelation.OppositeDirection
                            ? "opp"
                            : "unk")
                    .Append("/canon=")
                    .Append(segment.HasCanonicalDirection
                        ? FormatCanonicalSide(segment.LocalAlongCanonical) + ":" + FormatCanonicalSide(segment.ExpressAlongCanonical)
                        : "none");
                segmentWritten++;
            }

            if (segmentWritten == 0)
                debug.Append("none-in-local");

            return debug.Append(BuildSharedWindowNoMatchPairAttemptSummary(
                localChain,
                localProtectedInterval,
                expressChain,
                localSharedRuns,
                expressSharedRuns)).ToString();
        }

        private string BuildSharedWindowNoMatchPairAttemptSummary(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            LineTrackChain expressChain,
            List<SharedTrackRun> localSharedRuns,
            List<SharedTrackRun> expressSharedRuns)
        {
            if (localChain == null
                || expressChain == null
                || localSharedRuns == null
                || localSharedRuns.Count == 0
                || expressSharedRuns == null
                || expressSharedRuns.Count == 0)
            {
                return string.Empty;
            }

            var localBands = new List<SharedRunBand>();
            var expressBands = new List<SharedRunBand>();
            BuildSharedRunBands(localChain, localSharedRuns, localBands);
            BuildSharedRunBands(expressChain, expressSharedRuns, expressBands);

            StringBuilder debug = new StringBuilder();
            debug.Append(" pairTry=");
            int attemptCount = 0;
            for (int localIndex = 0; localIndex < localBands.Count && attemptCount < 4; localIndex++)
            {
                SharedRunBand localBand = localBands[localIndex];
                int clippedLocalStart = math.max(localBand.StartAtomIndex, localProtectedInterval.StartAtomIndex);
                int clippedLocalEndExclusive = math.min(localBand.EndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                if (clippedLocalEndExclusive <= clippedLocalStart)
                    continue;

                BypassProtectedInterval localWindow = BuildAtomWindowInterval(localChain, clippedLocalStart, clippedLocalEndExclusive);
                if (localWindow.EndAtomIndexExclusive <= localWindow.StartAtomIndex)
                    continue;

                for (int expressIndex = 0; expressIndex < expressBands.Count && attemptCount < 4; expressIndex++)
                {
                    SharedRunBand expressBand = expressBands[expressIndex];
                    BypassProtectedInterval expressWindow = BuildAtomWindowInterval(expressChain, expressBand.StartAtomIndex, expressBand.EndAtomIndexExclusive);
                    if (expressWindow.EndAtomIndexExclusive <= expressWindow.StartAtomIndex)
                        continue;

                    if (attemptCount > 0)
                        debug.Append(",");

                    debug.Append("L").Append(clippedLocalStart).Append("..").Append(clippedLocalEndExclusive)
                        .Append("/E").Append(expressBand.StartAtomIndex).Append("..").Append(expressBand.EndAtomIndexExclusive);

                    int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                    if (overlapCount <= 0)
                    {
                        debug.Append(":overlap0");
                        attemptCount++;
                        continue;
                    }

                    int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localWindow, expressChain, expressWindow);
                    SharedTraversalRelation relation = ResolveSharedTraversalRelation(localChain, localBand, expressChain, expressBand);
                    if (orderedRun <= 0)
                    {
                        debug.Append(":run0/")
                            .Append(relation == SharedTraversalRelation.SameDirection
                                ? "same"
                                : relation == SharedTraversalRelation.OppositeDirection
                                    ? "opp"
                                    : "unk");
                        attemptCount++;
                        continue;
                    }

                    debug.Append(":ok")
                        .Append("/o=").Append(overlapCount)
                        .Append("/r=").Append(orderedRun)
                        .Append("/")
                        .Append(relation == SharedTraversalRelation.SameDirection
                            ? "same"
                            : relation == SharedTraversalRelation.OppositeDirection
                                ? "opp"
                                : "unk");
                    attemptCount++;
                }
            }

            if (attemptCount == 0)
                debug.Append("none");

            return debug.ToString();
        }

        private bool TryBuildSharedWindowAuditLineEntry(
            Entity localVehicle,
            Entity localLine,
            LineTrackChain localChain,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            float departureReleaseCoordinate,
            float intervalDisplayLength,
            Entity expressVehicle,
            Entity expressLine,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            LineTrackChain expressChain,
            StringBuilder lineAudit,
            ref bool sawRunningVehicle)
        {
            if (expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                return false;
            if (!m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState) || expressState != VehicleState.Running)
                return false;
            sawRunningVehicle = true;
            int localProtectedIntervalIndex = FindProtectedIntervalIndex(localChain, protectedInterval);
            if (localProtectedIntervalIndex < 0)
                return false;

            PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, currentBypassBuilding, expressChain);
            if (!sharedWindowMatch.Found)
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":no-shared-window");
                return false;
            }

            if (!TryResolveExpressConflictWindowForLocalConflict(
                    expressVehicle,
                    expressLine,
                    expressWaypoints,
                    expressChain,
                    localChain,
                    localProtectedIntervalIndex,
                    protectedInterval,
                    sharedWindowMatch,
                    out int expressProtectedIntervalIndex,
                    out BypassProtectedInterval expressProtectedInterval,
                    out int overlapCount,
                    out int orderedRun,
                    out string intervalResolutionSource))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":protected-interval-missing");
                return false;
            }

            string resolutionSuffix = intervalResolutionSource == "fallback"
                ? " src=fallback"
                : intervalResolutionSource == "shared-window"
                    ? " src=shared-window"
                    : string.Empty;

            if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
            {
                if (lineAudit != null)
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index)
                        .Append(":weak-physical-overlap overlap=").Append(overlapCount)
                        .Append(" run=").Append(orderedRun)
                        .Append(resolutionSuffix);
                }
                return false;
            }

            if (!TryGetVehicleTrackCursorCurrentFrame(expressVehicle, expressLine, expressWaypoints, expressChain, out VehicleTrackCursor expressCursor))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":cursor-fail").Append(resolutionSuffix);
                return false;
            }

            int localTraversalPhaseIndex = TryResolveStaticTraversalPhaseWindow(
                localChain,
                protectedInterval.StartAtomIndex,
                protectedInterval.EndAtomIndexExclusive,
                out int resolvedLocalTraversalPhaseIndex,
                out _,
                out _)
                ? resolvedLocalTraversalPhaseIndex
                : -1;
            if (!TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                    localChain,
                    protectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    localTraversalPhaseIndex,
                    expressCursor.AtomCursorIndex,
                    -1,
                    out GlobalSharedTrunkSegment selectedTrunkSegment))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":static-opposite-direction").Append(resolutionSuffix);
                return false;
            }

            if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":proj-fail");
                return false;
            }
            if (expressPosition.Confidence < 0.6f)
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":low-conf(").Append(expressPosition.Confidence.ToString("0.00")).Append(")");
                return false;
            }

            RelativeToTrunkState expressTrunkState = ResolveVehicleTrunkTravelState(
                expressPosition,
                selectedTrunkSegment,
                useLocalSide: false);
            if (!selectedTrunkSegment.HasCanonicalDirection
                || !IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, selectedTrunkSegment))
            {
                if (lineAudit != null)
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index)
                        .Append(":trunk-state=")
                        .Append(FormatRelativeToTrunkState(expressTrunkState))
                        .Append(" localCanon=")
                        .Append(FormatCanonicalSide(selectedTrunkSegment.LocalAlongCanonical))
                        .Append(" expressCanon=")
                        .Append(FormatCanonicalSide(selectedTrunkSegment.ExpressAlongCanonical))
                        .Append(resolutionSuffix);
                }
                return false;
            }

            float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                expressPosition,
                expressProtectedInterval,
                intervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);

            if (TryDescribeExpressReleaseWindowClear(
                    departureReleaseCoordinate,
                    expressPosition,
                    expressCoordinate,
                    includeExpress,
                    out string releaseReason,
                    out string releasePositionText))
            {
                if (lineAudit != null)
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(releaseReason)
                        .Append("(").Append(releasePositionText).Append(resolutionSuffix).Append(")");
                }
                return false;
            }

            if (!TryGetActiveConflictCorridorCurrent(
                    localChain,
                    protectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    expressPosition,
                    out _,
                    out _,
                    out _))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":no-active-conflict-corridor").Append(resolutionSuffix);
                return false;
            }
            if (!includeExpress)
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":window-map-failed express=0").Append(resolutionSuffix);
                return false;
            }

            if (lineAudit != null)
            {
                if (TryGetSharedWindowPairState(localVehicle, localProtectedIntervalIndex, expressVehicle, out string pairState))
                {
                    string stateText = pairState.StartsWith("finalReject|", StringComparison.Ordinal)
                        ? pairState.Substring("finalReject|".Length)
                        : pairState;
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(stateText).Append(resolutionSuffix);
                }
                else
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":candidate").Append(resolutionSuffix);
                }
            }
            return true;
        }

        private string BuildSharedWindowAuditSummary(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            BypassProtectedInterval protectedInterval)
        {
            if (!TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                return "local=proj-fail";
            if (localPosition.Confidence < 0.6f)
                return "local=low-conf(" + localPosition.Confidence.ToString("0.00") + ")";

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out _))
                return "local=protected-interval-missing";

            StringBuilder sharedWindowAudit = new StringBuilder();
            sharedWindowAudit.Append("local=rel=").Append(localPosition.RelativeToProtectedInterval)
                .Append(" conf=")
                .Append(localPosition.Confidence >= 0.9f ? "high" : "ok");
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(localChain, protectedInterval, currentBypassBuilding);
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(protectedInterval);

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !routeVehicleBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteVehicle> routeVehicles))
                {
                    continue;
                }

                if (!TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                {
                    sharedWindowAudit.Append(" | line=").Append(expressLine.Index).Append(" chain-missing");
                    continue;
                }

                EnsureTrackChainBypassPipelineReady(expressChain);
                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, currentBypassBuilding, expressChain);
                if (!sharedWindowMatch.Found)
                {
                    int expressSharedRunCountForLocal = expressChain.SharedRunsByOtherLine.TryGetValue(localLine, out List<SharedTrackRun> expressRunsForLocal)
                        && expressRunsForLocal != null
                        ? expressRunsForLocal.Count
                        : 0;
                    sharedWindowAudit.Append(" | line=").Append(expressLine.Index)
                        .Append(" match=none runs=").Append(expressSharedRunCountForLocal)
                        .Append(BuildSharedWindowNoMatchDebugSummary(
                            localChain,
                            protectedInterval,
                            currentBypassBuilding,
                            expressChain));
                    continue;
                }

                StringBuilder lineAudit = new StringBuilder();
                lineAudit.Append("line=").Append(expressLine.Index);

                if (sharedWindowMatch.Ambiguous)
                {
                    lineAudit.Append(" match=ambiguous overlap=").Append(sharedWindowMatch.OverlapCount)
                        .Append(" run=").Append(sharedWindowMatch.OrderedRun);
                    sharedWindowAudit.Append(" | ").Append(lineAudit);
                    continue;
                }

                lineAudit.Append(" match=found overlap=").Append(sharedWindowMatch.OverlapCount)
                    .Append(" run=").Append(sharedWindowMatch.OrderedRun)
                    .Append(" window=").Append(sharedWindowMatch.ExpressSharedWindow.StartAtomIndex)
                    .Append("..").Append(sharedWindowMatch.ExpressSharedWindow.EndAtomIndexExclusive);

                GlobalSharedTrunkSnapshot trunkSnapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
                if (trunkSnapshot != null && trunkSnapshot.Segments.Count > 0)
                {
                    lineAudit.Append(" trunks=");
                    int trunkLimit = math.min(trunkSnapshot.Segments.Count, 3);
                    for (int trunkIndex = 0; trunkIndex < trunkLimit; trunkIndex++)
                    {
                        if (trunkIndex > 0)
                            lineAudit.Append(",");

                        GlobalSharedTrunkSegment trunk = trunkSnapshot.Segments[trunkIndex];
                        lineAudit.Append("L").Append(trunk.LocalCorridorStartAtomIndex).Append("..").Append(trunk.LocalCorridorEndAtomIndexExclusive)
                            .Append("/E").Append(trunk.ExpressCorridorStartAtomIndex).Append("..").Append(trunk.ExpressCorridorEndAtomIndexExclusive)
                            .Append("/")
                            .Append(trunk.TraversalRelation == SharedTraversalRelation.SameDirection
                                ? "same"
                                : trunk.TraversalRelation == SharedTraversalRelation.OppositeDirection
                                    ? "opp"
                                    : "unk")
                            .Append("/canon=")
                            .Append(trunk.HasCanonicalDirection
                                ? FormatCanonicalSide(trunk.LocalAlongCanonical) + ":" + FormatCanonicalSide(trunk.ExpressAlongCanonical)
                                : "none");
                    }
                }

                bool sawRunningVehicle = false;
                for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = routeVehicles[rvIndex].m_Vehicle;
                    if (expressVehicle == Entity.Null || expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                        continue;

                    if (!m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressVehicleState)
                        || expressVehicleState != VehicleState.Running)
                    {
                        continue;
                    }

                    sawRunningVehicle = true;
                    TryBuildSharedWindowAuditLineEntry(
                        localVehicle,
                        localLine,
                        localChain,
                        protectedInterval,
                        currentBypassBuilding,
                        departureReleaseCoordinate,
                        intervalDisplayLength,
                        expressVehicle,
                        expressLine,
                        expressWaypoints,
                        expressChain,
                        lineAudit,
                        ref sawRunningVehicle);
                }

                if (!sawRunningVehicle)
                    lineAudit.Append(" vehicles=none-running");

                sharedWindowAudit.Append(" | ").Append(lineAudit);
            }

            return sharedWindowAudit.ToString();
        }

        private static bool ShouldIncludeBypassSequenceForDecisionLog(bool hasDecision, BypassTrackModelDecision decision)
        {
            if (!hasDecision)
                return false;
            if (!decision.ShouldYield || decision.BlockerVehicle == Entity.Null)
                return true;
            if (decision.ReasonCode == "shared-window-match-ambiguous"
                || decision.ReasonCode == "local-runtime-position-unknown"
                || decision.ReasonCode == "local-runtime-position-low-confidence")
            {
                return true;
            }

            return decision.UsedFallbackResolution;
        }

        private static bool ShouldIncludeBypassSequenceForCompareLog(string alignment, BypassTrackModelDecision decision)
        {
            return alignment != "match" || ShouldIncludeBypassSequenceForDecisionLog(true, decision);
        }

        private void LogBypassTrackModelDecisionOnce(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding)
        {
            if (localVehicle == Entity.Null || localLine == Entity.Null)
                return;

            EnsureBypassTrackModelDecisionSnapshotCurrent(
                localVehicle,
                localLine,
                localWaypoints,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding,
                out BypassTrackModelDecision trackModelDecision);

            bool hasDecision = trackModelDecision.Available;
            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = (hasDecision ? (trackModelDecision.ShouldYield ? "Y" : "N") : "U")
                + "|"
                + (hasDecision ? trackModelDecision.ReasonCode : "decision-unavailable")
                + "|"
                + (hasDecision ? trackModelDecision.ProtectedIntervalIndex : -1)
                + "|"
                + currentBypassBuilding.Index
                + "|"
                + nextBypassBuilding.Index
                + "|"
                + (hasDecision ? trackModelDecision.BlockerVehicle.Index : -1);
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_BypassTrackModelDecisionThrottleCache,
                    m_BypassTrackModelDecisionLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            string summary = "trackModel[unavailable]";
            string trackModelRisk = "unavailable";
            if (TryEvaluateBypassTrackModelSummary(localLine, localWaypoints, currentWaypointIndex, out _, out string risk, out string builtSummary))
            {
                summary = builtSummary;
                trackModelRisk = risk;
            }

            string localPositionText = "pos[unknown]";
            string blockerPositionText = string.Empty;
            if (TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain)
                && TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out BypassProtectedInterval protectedInterval))
            {
                int localProtectedIntervalIndex = FindProtectedIntervalIndex(localChain, protectedInterval);
                if (TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                    localPositionText = FormatRuntimePosition(localPosition);

                if (trackModelDecision.BlockerVehicle != Entity.Null
                    && ResolveVehicleLine(trackModelDecision.BlockerVehicle) is Entity blockerLine
                    && blockerLine != Entity.Null
                    && GetBufferLookup<RouteWaypoint>(true).TryGetBuffer(blockerLine, out DynamicBuffer<RouteWaypoint> blockerWaypoints)
                    && TryGetLineTrackChain(blockerLine, blockerWaypoints, out LineTrackChain blockerChain)
                    && localProtectedIntervalIndex >= 0
                    && TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
                        trackModelDecision.BlockerVehicle,
                        blockerLine,
                        blockerWaypoints,
                        blockerChain,
                        localChain,
                        localProtectedIntervalIndex,
                        protectedInterval,
                        out _,
                        out BypassProtectedInterval blockerProtectedInterval,
                        out string intervalResolutionSource)
                    && TryProjectTrackModelRuntimePosition(trackModelDecision.BlockerVehicle, blockerLine, blockerWaypoints, blockerProtectedInterval, out TrackModelRuntimePosition blockerPosition))
                {
                    blockerPositionText = FormatRuntimePosition(blockerPosition);
                    if (intervalResolutionSource == "fallback")
                        blockerPositionText += " src=fallback";
                }
            }

                bool includeDecisionSequence = hasDecision && ShouldIncludeBypassSequenceForDecisionLog(hasDecision, trackModelDecision);
                string decisionSequence = includeDecisionSequence
                ? BuildBypassDecisionSequenceSummaryForLogging(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                        trackModelDecision.ProtectedIntervalIndex)
                    : string.Empty;
            string key = summary
                + "|"
                + trackModelRisk
                + "|"
                + (hasDecision ? trackModelDecision.ProtectedIntervalIndex : -1)
                + "|"
                + (hasDecision ? (trackModelDecision.ShouldYield ? "Y" : "N") + "|" + trackModelDecision.ReasonCode : "decision-unavailable")
                + "|"
                + currentBypassBuilding.Index
                + "|"
                + nextBypassBuilding.Index
                + "|"
                + (hasDecision ? trackModelDecision.BlockerVehicle.Index : -1)
                + "|"
                + localPositionText
                + "|"
                + blockerPositionText
                + "|"
                + decisionSequence;
            if (m_BypassTrackModelDecisionLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelDecisionLogCache[localVehicle] = key;

            log.Info("[BypassTrackModelDecision] vehicle=" + localVehicle.Index
                + " line=" + localLine.Index
                + " current=" + currentBypassBuilding.Index
                + " next=" + nextBypassBuilding.Index
                + " risk=" + trackModelRisk
                + " decision=" + (hasDecision ? (trackModelDecision.ShouldYield ? "yield" : "pass") : "unavailable")
                + " reason=" + (hasDecision ? trackModelDecision.ReasonCode : "decision-unavailable")
                + " local=" + localPositionText
                + " blocker=" + blockerPositionText
                + (!string.IsNullOrEmpty(decisionSequence) ? " " + decisionSequence : string.Empty)
                + " " + summary);
        }

        private void EnsureBypassTrackModelDecisionSnapshotCurrent(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out BypassTrackModelDecision trackModelDecision)
        {
            trackModelDecision = new BypassTrackModelDecision(false, false, "decision-unavailable", -1, false, Entity.Null, false);

            if (localVehicle == Entity.Null || localLine == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_BypassTrackModelDecisionSnapshots.TryGetValue(localVehicle, out BypassTrackModelDecisionSnapshot snapshot)
                && snapshot.Frame == nowFrame
                && snapshot.Line == localLine
                && snapshot.CurrentWaypointIndex == currentWaypointIndex
                && snapshot.CurrentBypassBuilding == currentBypassBuilding
                && snapshot.NextBypassBuilding == nextBypassBuilding)
            {
                trackModelDecision = snapshot.Decision;
                return;
            }

            TryEvaluateBypassTrackModelDecision(localVehicle, localLine, localWaypoints, currentWaypointIndex, nowFrame, out trackModelDecision);

            m_BypassTrackModelDecisionSnapshots[localVehicle] = new BypassTrackModelDecisionSnapshot(
                nowFrame,
                localLine,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding,
                trackModelDecision);
        }

        private string GetBypassTrackModelDecisionSuffix(Entity localVehicle, bool shouldYield)
        {
            if (!TryGetLatestBypassTrackModelDecisionSnapshot(localVehicle, out BypassTrackModelDecisionSnapshot snapshot))
            {
                return string.Empty;
            }

            BypassTrackModelDecision decision = snapshot.Decision;
            string trackModelDecision = decision.ShouldYield ? "yield" : "pass";
            string alignment = trackModelDecision == (shouldYield ? "yield" : "pass") ? "match" : "diff";
            return " trackModel=" + trackModelDecision + "/" + decision.ReasonCode + "/" + alignment;
        }

        private void LogBypassTrackModelDecisionComparison(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            bool shouldYield)
        {
            if (!TryGetLatestBypassTrackModelDecisionSnapshot(localVehicle, out BypassTrackModelDecisionSnapshot snapshot))
            {
                return;
            }

            BypassTrackModelDecision decision = snapshot.Decision;
            string summary = "trackModel[unavailable]";
            string risk = "unavailable";
            if (TryEvaluateBypassTrackModelSummary(localLine, localWaypoints, currentWaypointIndex, out _, out string builtRisk, out string builtSummary))
            {
                summary = builtSummary;
                risk = builtRisk;
            }
            string localPositionText = decision.HasReliableLocalPosition ? "pos[known]" : "pos[unknown]";
            string blockerPositionText = decision.BlockerVehicle != Entity.Null
                ? (decision.UsedFallbackResolution ? "blocker src=fallback" : "blocker")
                : string.Empty;
            string liveDecision = shouldYield ? "yield" : "pass";
            string trackModelDecision = decision.ShouldYield ? "yield" : "pass";
            string alignment = trackModelDecision == liveDecision ? "match" : "diff";
            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = liveDecision
                + "|"
                + trackModelDecision
                + "|"
                + alignment
                + "|"
                + decision.ReasonCode
                + "|"
                + decision.ProtectedIntervalIndex
                + "|"
                + decision.BlockerVehicle.Index;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_BypassTrackModelCompareThrottleCache,
                    m_BypassTrackModelCompareLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            bool includeDecisionSequence = ShouldIncludeBypassSequenceForCompareLog(alignment, decision);
            string decisionSequence = includeDecisionSequence
                ? BuildBypassDecisionSequenceSummaryForLogging(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    decision.ProtectedIntervalIndex)
                : string.Empty;
            string key = liveDecision
                + "|"
                + trackModelDecision
                + "|"
                + decision.ReasonCode
                + "|"
                + decision.ProtectedIntervalIndex
                + "|"
                + decision.BlockerVehicle.Index
                + "|"
                + localPositionText
                + "|"
                + blockerPositionText
                + "|"
                + decisionSequence;
            if (m_BypassTrackModelCompareLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelCompareLogCache[localVehicle] = key;
            log.Info("[BypassTrackModelCompare] vehicle=" + localVehicle.Index
                + " live=" + liveDecision
                + " trackModel=" + trackModelDecision
                + " reason=" + decision.ReasonCode
                + " risk=" + risk
                + " compare=" + alignment
                + " protectedInterval=" + decision.ProtectedIntervalIndex
                + " local=" + localPositionText
                + " blocker=" + blockerPositionText
                + (!string.IsNullOrEmpty(decisionSequence) ? " " + decisionSequence : string.Empty)
                + " " + summary);
        }

    }
}
