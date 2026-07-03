using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackDiag
    {
        private readonly Dictionary<Entity, List<DevSightLaneOccurrence>> m_DevSightLaneIndex = new Dictionary<Entity, List<DevSightLaneOccurrence>>();
        private readonly TrackSupport m_Support;
        private TrackBuild m_Build;
        private SharedIndex m_Shared;
        private TrackIntervals m_Intervals;

        internal TrackDiag(TrackSupport support)
        {
            m_Support = support;
        }

        internal void Bind(TrackBuild build, SharedIndex shared, TrackIntervals intervals)
        {
            m_Build = build;
            m_Shared = shared;
            m_Intervals = intervals;
        }

        private EntityManager EntityManager => m_Support.EntityManager;
        private TimedLogger log => m_Support.Log;

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
                    m_Support.TryGetRenderedLabelName(building, out string name);
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
                    m_Support.TryGetRenderedLabelName(building, out string name);
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

        internal string FormatSharedMapStationLabel(Entity building)
        {
            if (building != Entity.Null)
            {
                try
                {
                    m_Support.TryGetRenderedLabelName(building, out string name);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "stop";
        }

        internal string FormatSharedMapVehicleNameLabel(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                try
                {
                    m_Support.TryGetRenderedLabelName(vehicle, out string name);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "vehicle";
        }

        internal string ResolveTrackModelLineLabel(Entity line, bool includeEntityFallback)
        {
            if (line == Entity.Null)
                return includeEntityFallback ? "line" : string.Empty;

            try
            {
                if (m_Support.TryGetCustomLineName(line, out string customName)
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
                m_Support.TryGetRenderedLabelName(line, out string rendered);
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

        internal string FormatSharedMapLineLabel(Entity line)
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
                TrackAtomClass atomClass = m_Build.ClassifyPathElementTarget(element);
                TrackTraversalDir traversalDir = m_Build.ResolveTraversalDirection(pathElements, pathIndex);
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
            m_Shared.EnsureSharedTrackIndexCurrent();

            int sharedAtomKeys = 0;
            int sharedOccurrences = 0;
            var contextCountByPhysicalTarget = new Dictionary<Entity, int>();
            var adjacencyByPhysicalTarget = new Dictionary<Entity, HashSet<string>>();
            foreach (KeyValuePair<TrackAtomKey, List<SharedTrackOccurrence>> entry in m_Shared.Track)
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

            log.Info("[TrackModelShared] keys=" + m_Shared.Track.Count
                + " physicalTargets=" + contextCountByPhysicalTarget.Count
                + " sharedKeys=" + sharedAtomKeys
                + " sharedOccurrences=" + sharedOccurrences
                + " contextSplitTargets=" + contextSplitTargets
                + " mirroredTargets=" + mirroredTargets);
        }

        private static string FormatTurnbackBoundaryLabel(LineTrackChain chain, TurnbackBoundary boundary)
        {
            return "atom=" + boundary.AtomIndex
                + " slices=" + boundary.BeforeSliceIndex + "->" + boundary.AfterSliceIndex
                + " event=" + boundary.BoundaryEventIndex;
        }

        internal void LogLineTrackChainDiagnostics(Entity line)
        {
            if (!RtLog.VerboseEnabled)
                return;

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

            if (!m_Build.TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                log.Info("[TrackModel] line=" + line.Index + " chain=unavailable");
                return;
            }

            m_Intervals.EnsureBypassPipelineReady(chain);

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

        internal void AddDevSightChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || entry.Value == null
                    || entry.Value.Count == 0)
                {
                    continue;
                }

                if (!m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences))
                {
                    occurrences = new List<DevSightLaneOccurrence>();
                    m_DevSightLaneIndex[entry.Key] = occurrences;
                }

                occurrences.Add(new DevSightLaneOccurrence(chain.LineEntity, chain, entry.Value));
            }
        }

        internal void RemoveDevSightChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || !m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                for (int i = occurrences.Count - 1; i >= 0; i--)
                {
                    if (occurrences[i].LineEntity == chain.LineEntity)
                        occurrences.RemoveAt(i);
                }

                if (occurrences.Count == 0)
                    m_DevSightLaneIndex.Remove(entry.Key);
            }
        }

        internal void ClearAll()
        {
            m_DevSightLaneIndex.Clear();
        }

        internal void ClearLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<Entity> keysToRemove = null;
            foreach (KeyValuePair<Entity, List<DevSightLaneOccurrence>> entry in m_DevSightLaneIndex)
            {
                List<DevSightLaneOccurrence> occurrences = entry.Value;
                if (occurrences == null)
                    continue;

                for (int i = occurrences.Count - 1; i >= 0; i--)
                {
                    if (occurrences[i].LineEntity == line)
                        occurrences.RemoveAt(i);
                }

                if (occurrences.Count == 0)
                {
                    keysToRemove ??= new List<Entity>();
                    keysToRemove.Add(entry.Key);
                }
            }

            if (keysToRemove == null)
                return;

            for (int i = 0; i < keysToRemove.Count; i++)
                m_DevSightLaneIndex.Remove(keysToRemove[i]);
        }

        internal string BuildDevSightTooltipSummary(Entity laneEntity)
        {
            if (laneEntity == Entity.Null)
                return "target  null";

            bool hasIndex = m_DevSightLaneIndex.TryGetValue(laneEntity, out List<DevSightLaneOccurrence> occurrences);

            if (!hasIndex)
            {
                foreach (KeyValuePair<Entity, List<DevSightLaneOccurrence>> kvp in m_DevSightLaneIndex)
                {
                    if (kvp.Key.Index == laneEntity.Index)
                    {
                        occurrences = kvp.Value;
                        hasIndex = true;
                        break;
                    }
                }
            }

            if (!hasIndex || occurrences == null || occurrences.Count == 0)
                return "target  " + FormatEntityRef(laneEntity) + "\ntrack model  no-chain-hit";

            StringBuilder result = new StringBuilder(256);
            result.Append("target  ").Append(FormatEntityRef(laneEntity));

            for (int i = 0; i < occurrences.Count; i++)
                result.Append('\n').Append(FormatDevSightOccurrence(occurrences[i]));

            return result.ToString();
        }

        private string FormatDevSightOccurrence(DevSightLaneOccurrence occurrence)
        {
            string lineLabel = ResolveTrackModelLineLabel(occurrence.LineEntity, includeEntityFallback: false);
            return lineLabel + " atoms=" + FormatDevSightAtomIndices(occurrence.AtomIndices);
        }

        private string FormatDevSightAtomIndices(List<int> atomIndices)
        {
            if (atomIndices == null || atomIndices.Count == 0)
                return "[]";

            if (atomIndices.Count <= 3)
                return "[" + string.Join(",", atomIndices) + "]";

            return "[" + atomIndices[0] + "," + atomIndices[1] + ".." + atomIndices[atomIndices.Count - 1] + " x" + atomIndices.Count + "]";
        }

        private static string FormatEntityRef(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index + ":" + entity.Version;
        }

    }
}
