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
    internal sealed class TrackDump
    {
        private readonly TrackSupport m_Support;
        private readonly TrackBuild m_Build;
        private readonly SharedIndex m_Shared;
        private readonly TrackIntervals m_Intervals;
        private readonly TrackDiag m_Diag;

        internal TrackDump(
            TrackSupport support,
            TrackBuild build,
            SharedIndex shared,
            TrackIntervals intervals,
            TrackDiag diag)
        {
            m_Support = support;
            m_Build = build;
            m_Shared = shared;
            m_Intervals = intervals;
            m_Diag = diag;
        }

        private EntityManager EntityManager => m_Support.EntityManager;
        private TimedLogger log => m_Support.Log;

        private readonly struct SharedPhysicalCorridorRef
        {
            public readonly Entity LineEntity;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly bool HasMirroredContext;
            public readonly int SharedLineCount;

            public SharedPhysicalCorridorRef(
                Entity lineEntity,
                int startAtomIndex,
                int endAtomIndexExclusive,
                bool hasMirroredContext,
                int sharedLineCount)
            {
                LineEntity = lineEntity;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                HasMirroredContext = hasMirroredContext;
                SharedLineCount = sharedLineCount;
            }
        }

        private readonly struct AggregatedCorridorSpan
        {
            public readonly Entity LineEntity;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly bool HasMirroredContext;
            public readonly int SharedLineCount;

            public AggregatedCorridorSpan(
                Entity lineEntity,
                int startAtomIndex,
                int endAtomIndexExclusive,
                bool hasMirroredContext,
                int sharedLineCount)
            {
                LineEntity = lineEntity;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                HasMirroredContext = hasMirroredContext;
                SharedLineCount = sharedLineCount;
            }
        }

        private readonly struct CorridorStationAnchor
        {
            public readonly Entity Building;
            public readonly float Position;
            public readonly bool HasStopAnchor;

            public CorridorStationAnchor(Entity building, float position, bool hasStopAnchor)
            {
                Building = building;
                Position = position;
                HasStopAnchor = hasStopAnchor;
            }
        }

        private const float STATION_ANCHOR_MERGE_DISTANCE = 12f;
        private const float STATION_VEHICLE_ATTACH_DISTANCE = 1.25f;
        private const double SimFramesPerMinute = 182.044;

        private readonly struct SharedPhysicalCorridorAuditEntry
        {
            public readonly string CorridorLabel;
            public readonly string LineLabel;
            public readonly Entity Vehicle;
            public readonly string State;
            public readonly bool Included;
            public readonly string Reason;
            public readonly int AtomIndex;
            public readonly float AtomPosition01;
            public readonly float Confidence;

            public SharedPhysicalCorridorAuditEntry(
                string corridorLabel,
                string lineLabel,
                Entity vehicle,
                string state,
                bool included,
                string reason,
                int atomIndex,
                float atomPosition01,
                float confidence)
            {
                CorridorLabel = corridorLabel;
                LineLabel = lineLabel;
                Vehicle = vehicle;
                State = state;
                Included = included;
                Reason = reason;
                AtomIndex = atomIndex;
                AtomPosition01 = atomPosition01;
                Confidence = confidence;
            }
        }

        private void LogIndependentSharedPhysicalCorridorDump(NativeArray<Entity> lines)
        {
            Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> sharedIndex = BuildIndependentSharedTrackIndex(lines);
            Dictionary<ulong, List<SharedPhysicalCorridorRef>> corridorGroups = new Dictionary<ulong, List<SharedPhysicalCorridorRef>>();

            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!m_Build.TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                    continue;

                List<SharedTrackRun> runs = BuildIndependentSharedRuns(line, chain, sharedIndex);
                for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    SharedTrackRun run = runs[runIndex];
                    ulong signature = ComputeParticipatingLineSignature(line, chain, run.StartAtomIndex, run.EndAtomIndexExclusive, sharedIndex);
                    if (signature == 0UL)
                        continue;

                    if (!corridorGroups.TryGetValue(signature, out List<SharedPhysicalCorridorRef> group))
                    {
                        group = new List<SharedPhysicalCorridorRef>();
                        corridorGroups[signature] = group;
                    }

                    group.Add(new SharedPhysicalCorridorRef(
                        line,
                        run.StartAtomIndex,
                        run.EndAtomIndexExclusive,
                        run.HasMirroredContext,
                        run.SharedLineCount));
                }
            }

            List<string> rows = new List<string>(corridorGroups.Count);
            List<string> audits = new List<string>();
            foreach (KeyValuePair<ulong, List<SharedPhysicalCorridorRef>> entry in corridorGroups)
            {
                if (TryBuildIndependentSharedPhysicalCorridorRow(entry.Value, out string row, out List<string> rowAudits))
                {
                    rows.Add(row);
                    audits.AddRange(rowAudits);
                }
            }

            rows.Sort(StringComparer.Ordinal);
            audits.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModelDumpTable]").AppendLine();
            for (int i = 0; i < rows.Count; i++)
                sb.Append(rows[i]).AppendLine();
            sb.Append("[TrackModelDumpAudit]").AppendLine();
            for (int i = 0; i < audits.Count; i++)
                sb.Append(audits[i]).AppendLine();

            log.Info(sb.ToString().TrimEnd());
        }

        private Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> BuildIndependentSharedTrackIndex(NativeArray<Entity> lines)
        {
            Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> index = new Dictionary<TrackAtomKey, List<SharedTrackOccurrence>>();

            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!m_Build.TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                    continue;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (!index.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences))
                    {
                        occurrences = new List<SharedTrackOccurrence>();
                        index[atom.Key] = occurrences;
                    }

                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, SharedIndex.ResolveWaypointSegmentIndex(chain, atomIndex)));
                }
            }

            return index;
        }

        private static ulong ComputeParticipatingLineSignature(
            Entity line,
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> sharedIndex)
        {
            if (chain == null || sharedIndex == null)
                return 0UL;

            HashSet<int> lineIndices = new HashSet<int>();
            lineIndices.Add(line.Index);

            for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;
                if (!sharedIndex.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences) || occurrences == null)
                    continue;

                for (int i = 0; i < occurrences.Count; i++)
                    lineIndices.Add(occurrences[i].LineEntity.Index);
            }

            if (lineIndices.Count <= 1)
                return 0UL;

            List<int> sorted = new List<int>(lineIndices);
            sorted.Sort();
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < sorted.Count; i++)
                hash = MixLineSignature(hash, sorted[i]);
            hash = MixLineSignature(hash, sorted.Count);
            return hash;
        }

        private List<SharedTrackRun> BuildIndependentSharedRuns(
            Entity line,
            LineTrackChain chain,
            Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> sharedIndex)
        {
            List<SharedTrackRun> runs = new List<SharedTrackRun>();
            int runStart = -1;
            bool runMirrored = false;
            int runSharedLineCount = 0;

            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane
                    || !TryGetIndependentSharedAtomContext(line, atom.Key, sharedIndex, out int sharedLineCount, out bool mirroredContext))
                {
                    if (runStart >= 0)
                    {
                        runs.Add(new SharedTrackRun(runStart, atomIndex, runMirrored, runSharedLineCount));
                        runStart = -1;
                        runMirrored = false;
                        runSharedLineCount = 0;
                    }

                    continue;
                }

                if (runStart < 0)
                {
                    runStart = atomIndex;
                    runMirrored = mirroredContext;
                    runSharedLineCount = sharedLineCount;
                    continue;
                }

                runMirrored |= mirroredContext;
                runSharedLineCount = math.max(runSharedLineCount, sharedLineCount);
            }

            if (runStart >= 0)
                runs.Add(new SharedTrackRun(runStart, chain.TrackAtoms.Count, runMirrored, runSharedLineCount));

            return runs;
        }

        private static bool TryGetIndependentSharedAtomContext(
            Entity line,
            TrackAtomKey key,
            Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> sharedIndex,
            out int sharedLineCount,
            out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;
            if (!sharedIndex.TryGetValue(key, out List<SharedTrackOccurrence> occurrences)
                || occurrences == null
                || occurrences.Count == 0)
            {
                return false;
            }

            HashSet<Entity> sharedLines = new HashSet<Entity>();
            for (int i = 0; i < occurrences.Count; i++)
            {
                if (occurrences[i].LineEntity != line)
                    sharedLines.Add(occurrences[i].LineEntity);
            }

            sharedLineCount = sharedLines.Count;
            if (sharedLineCount == 0)
                return false;

            TrackAtomKey mirroredKey = new TrackAtomKey(key.PhysicalLaneKey, key.NextTarget, key.PreviousTarget);
            if (sharedIndex.TryGetValue(mirroredKey, out List<SharedTrackOccurrence> mirroredOccurrences)
                && mirroredOccurrences != null)
            {
                for (int i = 0; i < mirroredOccurrences.Count; i++)
                {
                    if (mirroredOccurrences[i].LineEntity != line)
                    {
                        mirroredContext = true;
                        break;
                    }
                }
            }

            return true;
        }

        private static ulong ComputePhysicalCorridorSignature(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive)
        {
            List<int> laneIndices = new List<int>();
            for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                laneIndices.Add(atom.Key.PhysicalLaneKey.Index);
            }

            if (laneIndices.Count == 0)
                return 0UL;

            laneIndices.Sort();
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < laneIndices.Count; i++)
                hash = MixLineSignature(hash, laneIndices[i]);

            hash = MixLineSignature(hash, laneIndices.Count);
            return hash;
        }

        private bool TryBuildIndependentSharedPhysicalCorridorRow(
            List<SharedPhysicalCorridorRef> group,
            out string row,
            out List<string> auditRows)
        {
            row = string.Empty;
            auditRows = new List<string>();
            if (group == null || group.Count == 0)
                return false;

            List<AggregatedCorridorSpan> spans = AggregateCorridorSpans(group);
            if (spans.Count == 0)
                return false;

            AggregatedCorridorSpan reference = spans[0];
            int bestReferenceStationCount = CountControlPointsInSpan(reference);
            int bestReferenceLength = reference.EndAtomIndexExclusive - reference.StartAtomIndex;
            for (int i = 1; i < spans.Count; i++)
            {
                int stationCount = CountControlPointsInSpan(spans[i]);
                int spanLength = spans[i].EndAtomIndexExclusive - spans[i].StartAtomIndex;
                if (stationCount > bestReferenceStationCount
                    || (stationCount == bestReferenceStationCount && spanLength > bestReferenceLength))
                {
                    reference = spans[i];
                    bestReferenceStationCount = stationCount;
                    bestReferenceLength = spanLength;
                }
            }

            if (!EntityManager.HasBuffer<RouteWaypoint>(reference.LineEntity))
                return false;

            DynamicBuffer<RouteWaypoint> referenceWaypoints = EntityManager.GetBuffer<RouteWaypoint>(reference.LineEntity, true);
            if (!m_Build.TryGetLineTrackChain(reference.LineEntity, referenceWaypoints, out LineTrackChain referenceChain))
                return false;

            float displayLength = math.max(1f, reference.EndAtomIndexExclusive - reference.StartAtomIndex);
            List<int> referenceSequence = BuildRunLaneSequence(referenceChain, reference.StartAtomIndex, reference.EndAtomIndexExclusive);

            ResolveRunBoundaryLabels(referenceChain, reference.StartAtomIndex, reference.EndAtomIndexExclusive, out string startLabel, out string endLabel);
            if (TryResolveSyntheticLoopClosureEndLabel(
                referenceWaypoints,
                referenceChain,
                reference.StartAtomIndex,
                reference.EndAtomIndexExclusive,
                out string syntheticEndLabel))
            {
                endLabel = syntheticEndLabel;
            }
            string corridorLabel = startLabel + " -> " + endLabel;
            List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(group.Count * 6 + 8);
            items.Add(new TrackModelSequenceItem(0f, 0, startLabel));
            items.Add(new TrackModelSequenceItem(displayLength, 0, endLabel));
            List<CorridorStationAnchor> stationAnchors = BuildCorridorStationAnchors(
                spans,
                reference,
                referenceSequence,
                displayLength);
            AppendCorridorStationAnchors(items, stationAnchors, displayLength, startLabel, endLabel);

            HashSet<Entity> participatingLines = new HashSet<Entity>();
            for (int groupIndex = 0; groupIndex < spans.Count; groupIndex++)
            {
                AggregatedCorridorSpan corridorRef = spans[groupIndex];
                participatingLines.Add(corridorRef.LineEntity);
            }

            if (participatingLines.Count <= 1)
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

            StringBuilder seqBuilder = new StringBuilder();
            for (int i = 0; i < items.Count; )
            {
                if (seqBuilder.Length > 0)
                    seqBuilder.Append(" -> ");

                TrackModelSequenceItem item = items[i];
                if (item.KindOrder == 0)
                {
                    List<string> anchoredVehicles = new List<string>();
                    int j = i + 1;
                    while (j < items.Count
                        && items[j].KindOrder > 0
                        && math.abs(items[j].DistanceMeters - item.DistanceMeters) <= STATION_VEHICLE_ATTACH_DISTANCE)
                    {
                        anchoredVehicles.Add(items[j].Label);
                        j++;
                    }

                    seqBuilder.Append(item.Label);
                    if (anchoredVehicles.Count > 0)
                    {
                        seqBuilder.Append("[");
                        for (int k = 0; k < anchoredVehicles.Count; k++)
                        {
                            if (k > 0)
                                seqBuilder.Append(", ");
                            seqBuilder.Append(anchoredVehicles[k]);
                        }
                        seqBuilder.Append("]");
                    }

                    i = j;
                    continue;
                }

                seqBuilder.Append(item.Label);
                i++;
            }

            row = seqBuilder.ToString();
            return true;
        }

        private static List<AggregatedCorridorSpan> AggregateCorridorSpans(List<SharedPhysicalCorridorRef> group)
        {
            Dictionary<Entity, SharedPhysicalCorridorRef> merged = new Dictionary<Entity, SharedPhysicalCorridorRef>();
            for (int i = 0; i < group.Count; i++)
            {
                SharedPhysicalCorridorRef current = group[i];
                if (merged.TryGetValue(current.LineEntity, out SharedPhysicalCorridorRef existing))
                {
                    merged[current.LineEntity] = new SharedPhysicalCorridorRef(
                        current.LineEntity,
                        math.min(existing.StartAtomIndex, current.StartAtomIndex),
                        math.max(existing.EndAtomIndexExclusive, current.EndAtomIndexExclusive),
                        existing.HasMirroredContext || current.HasMirroredContext,
                        math.max(existing.SharedLineCount, current.SharedLineCount));
                }
                else
                {
                    merged[current.LineEntity] = current;
                }
            }

            List<AggregatedCorridorSpan> spans = new List<AggregatedCorridorSpan>(merged.Count);
            foreach (KeyValuePair<Entity, SharedPhysicalCorridorRef> entry in merged)
            {
                SharedPhysicalCorridorRef mergedRef = entry.Value;
                spans.Add(new AggregatedCorridorSpan(
                    mergedRef.LineEntity,
                    mergedRef.StartAtomIndex,
                    mergedRef.EndAtomIndexExclusive,
                    mergedRef.HasMirroredContext,
                    mergedRef.SharedLineCount));
            }

            spans.Sort((a, b) => a.LineEntity.Index.CompareTo(b.LineEntity.Index));
            return spans;
        }

        private void LogTrackModelReplayDump(NativeArray<Entity> allLines)
        {
            List<Entity> targetLines = CollectReplayDumpTargetLines(allLines);
            StringBuilder sb = new StringBuilder(32768);
            sb.Append("[TrackModelReplayDump]").AppendLine();
            sb.Append("time=").Append(SlotStr((int)(m_Support.FrameIndex / (uint)SimFramesPerMinute) % 1440))
              .Append(" lines=").Append(targetLines.Count)
              .Append(" managedVehicles=").Append(m_Support.ManagedVehicleCount)
              .AppendLine();

            for (int i = 0; i < targetLines.Count; i++)
            {
                Entity line = targetLines[i];
                AppendReplayLineDump(sb, line);
            }

            log.Info(sb.ToString().TrimEnd());
        }

        private List<Entity> CollectReplayDumpTargetLines(NativeArray<Entity> allLines)
        {
            HashSet<Entity> lines = new HashSet<Entity>();
            foreach (KeyValuePair<string, AppliedLine> entry in m_Support.AppliedLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line != Entity.Null && EntityManager.Exists(line))
                    lines.Add(line);
            }

            if (lines.Count == 0)
            {
                for (int i = 0; i < allLines.Length; i++)
                {
                    Entity line = allLines[i];
                    if (line != Entity.Null && EntityManager.Exists(line))
                        lines.Add(line);
                }
            }

            List<Entity> ordered = new List<Entity>(lines);
            ordered.Sort((a, b) =>
            {
                int cmp = string.CompareOrdinal(FormatReadableLineLabel(a), FormatReadableLineLabel(b));
                if (cmp != 0)
                    return cmp;
                return a.Index.CompareTo(b.Index);
            });
            return ordered;
        }

        private void AppendReplayLineDump(StringBuilder sb, Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (!m_Build.TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                sb.Append("line=").Append(line.Index).Append(" chain=unavailable").AppendLine();
                return;
            }

            m_Intervals.EnsureBypassPipelineReady(chain);

            sb.AppendLine("--- line ---");
            sb.Append("line=").Append(line.Index)
              .Append(" label=").Append(FormatReadableLineLabel(line))
              .Append(" local=").Append(IsAppliedLocal(line) ? "1" : "0")
              .Append(" express=").Append(IsAppliedExpress(line) ? "1" : "0")
              .Append(" waypoints=").Append(waypoints.Length)
              .Append(" segments=").Append(chain.SegmentRanges.Count)
              .Append(" atoms=").Append(chain.TrackAtoms.Count)
              .Append(" controlPoints=").Append(chain.ControlPoints.Count)
              .Append(" controlEdges=").Append(chain.ControlEdges.Count)
              .Append(" protectedIntervals=").Append(chain.BypassProtectedIntervals.Count)
              .Append(" protectedShared=").Append(chain.ProtectedSharedIntervals.Count)
              .AppendLine();

            AppendReplayWaypoints(sb, line, waypoints, chain);
            AppendReplayStationWindows(sb, waypoints, chain);
            AppendReplayRawSegments(sb, line);
            AppendReplayOfficialSegmentStructures(sb, line, waypoints);
            AppendReplayTrackAtoms(sb, chain);
            AppendReplaySegmentRanges(sb, chain);
            AppendReplayControlPoints(sb, chain);
            AppendReplayControlEdges(sb, chain);
            AppendReplaySharedRuns(sb, chain);
            AppendReplaySharedRunsByOtherLine(sb, chain);
            AppendReplayControlEdgeSharedSpans(sb, chain);
            AppendReplayProtectedIntervals(sb, chain);
        }

        private void AppendReplayWaypoints(StringBuilder sb, Entity line, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain chain)
        {
            sb.Append("waypoints:");
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity building = m_Support.GetStationBuildingForWaypoint(waypoints, i);
                int cpIndex = -1;
                int stationAtom = -1;
                int inAtom = -1;
                int outAtom = -1;
                ControlPointKind? cpKind = null;
                for (int cpSearch = 0; cpSearch < chain.ControlPoints.Count; cpSearch++)
                {
                    ControlPointMarker cp = chain.ControlPoints[cpSearch];
                    if (cp.WaypointIndex != i)
                        continue;
                    cpIndex = cpSearch;
                    stationAtom = cp.AtomIndex;
                    cpKind = cp.Kind;
                    break;
                }

                if (cpIndex >= 0)
                {
                    if (cpIndex > 0)
                    {
                        ControlEdge inEdge = chain.ControlEdges[cpIndex - 1];
                        inAtom = math.max(inEdge.StartAtomIndex, inEdge.EndAtomIndexExclusive - 1);
                    }

                    if (cpIndex < chain.ControlEdges.Count)
                    {
                        ControlEdge outEdge = chain.ControlEdges[cpIndex];
                        outAtom = outEdge.StartAtomIndex;
                    }
                }

                Entity waypointEntity = waypoints[i].m_Waypoint;
                string endpointInfo = "-";
                string endpointDetail = "";
                if (RouteWaypointEndpointResolver.TryResolveRouteWaypointEndpoint(EntityManager, waypointEntity, out RouteWaypointEndpoint endpoint))
                {
                    endpointInfo = $"{endpoint.Kind}/{endpoint.Direction}";

                    string startFlags = "-";
                    string startTrackTypes = "-";
                    string endFlags = "-";
                    string endTrackTypes = "-";
                    string connectedStatus = "-";
                    string ownerChain = "-";
                    string outsideName = "-";

                    if (endpoint.OutsideConnection != Entity.Null)
                    {
                        outsideName = FormatTrackModelDisplayStationLabel(endpoint.OutsideConnection, i);
                    }

                    if (endpoint.StartLane != Entity.Null && EntityManager.HasComponent<ConnectionLane>(endpoint.StartLane))
                    {
                        ConnectionLane cl = EntityManager.GetComponentData<ConnectionLane>(endpoint.StartLane);
                        startFlags = cl.m_Flags.ToString();
                        startTrackTypes = cl.m_TrackTypes.ToString();
                    }

                    if (endpoint.EndLane != Entity.Null && EntityManager.HasComponent<ConnectionLane>(endpoint.EndLane))
                    {
                        ConnectionLane cl = EntityManager.GetComponentData<ConnectionLane>(endpoint.EndLane);
                        endFlags = cl.m_Flags.ToString();
                        endTrackTypes = cl.m_TrackTypes.ToString();
                    }

                    if (EntityManager.HasComponent<Game.Routes.Connected>(waypointEntity))
                    {
                        Entity connected = EntityManager.GetComponentData<Game.Routes.Connected>(waypointEntity).m_Connected;
                        connectedStatus = connected.Index.ToString();

                        List<int> ownerChainIndices = new List<int>();
                        Entity current = connected;
                        for (int depth = 0; depth < 4 && current != Entity.Null; depth++)
                        {
                            if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(current))
                            {
                                ownerChainIndices.Add(current.Index);
                                break;
                            }
                            if (EntityManager.HasComponent<Game.Common.Owner>(current))
                            {
                                current = EntityManager.GetComponentData<Game.Common.Owner>(current).m_Owner;
                                if (current != Entity.Null)
                                    ownerChainIndices.Add(current.Index);
                            }
                            else
                            {
                                break;
                            }
                        }
                        if (ownerChainIndices.Count > 0)
                            ownerChain = string.Join("->", ownerChainIndices);
                    }

                    endpointDetail = $" outside={outsideName} startFlags={startFlags} startTrackTypes={startTrackTypes} endFlags={endFlags} endTrackTypes={endTrackTypes} connected={connectedStatus} ownerChain={ownerChain}";
                }

                sb.Append(" | wp").Append(i)
                  .Append(" target=").Append(waypointEntity.Index)
                  .Append(" stop=").Append(building.Index)
                  .Append(" label=").Append(FormatTrackModelDisplayStationLabel(building, i))
                  .Append(" bypass=").Append(GetBypassBuildingForWaypoint(waypoints, i) != Entity.Null ? "1" : "0")
                  .Append(" endpoint=").Append(endpointInfo)
                  .Append(endpointDetail)
                  .Append(" cp=").Append(cpIndex >= 0 ? cpIndex.ToString() : "-")
                  .Append(" kind=").Append(cpKind.HasValue ? cpKind.Value.ToString() : "-")
                  .Append(" atom=").Append(stationAtom >= 0 ? stationAtom.ToString() : "-")
                  .Append(" inAtom=").Append(inAtom >= 0 ? inAtom.ToString() : "-")
                  .Append(" outAtom=").Append(outAtom >= 0 ? outAtom.ToString() : "-");
            }
            sb.AppendLine();
        }

        private void AppendReplayStationWindows(StringBuilder sb, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain chain)
        {
            sb.Append("stationWindows:");
            if (chain == null || chain.TraversalProfile == null || chain.TraversalProfile.Events == null)
            {
                sb.Append(" unavailable").AppendLine();
                return;
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                bool found = false;
                for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
                {
                    TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                    if (traversalEvent.WaypointIndex != i
                        || (traversalEvent.Kind != TraversalEventKind.Stop && traversalEvent.Kind != TraversalEventKind.Pass))
                    {
                        continue;
                    }

                    int startAtom = traversalEvent.StartAtomIndex;
                    int endAtomExclusive = math.max(startAtom + 1, traversalEvent.EndAtomIndexExclusive);
                    int approachStartAtom = math.max(0, startAtom - Broadcasting.Runtime.BroadcastApproachRemainingAtomThreshold);
                    Entity building = traversalEvent.Building != Entity.Null
                        ? traversalEvent.Building
                        : m_Support.GetStationBuildingForWaypoint(waypoints, i);
                    sb.Append(" | wp").Append(i)
                      .Append(" label=").Append(FormatTrackModelDisplayStationLabel(building, i))
                      .Append(" kind=").Append(traversalEvent.Kind)
                      .Append(" event=").Append(traversalEvent.EventIndex)
                      .Append(" atoms=").Append(startAtom).Append("..").Append(endAtomExclusive)
                      .Append(" approach=").Append(approachStartAtom).Append("..").Append(startAtom)
                      .Append(" stopFrames=").Append(FormatEtaFrames(traversalEvent.StopFrames));
                    found = true;
                }

                if (!found)
                {
                    Entity building = m_Support.GetStationBuildingForWaypoint(waypoints, i);
                    sb.Append(" | wp").Append(i)
                      .Append(" label=").Append(FormatTrackModelDisplayStationLabel(building, i))
                      .Append(" window=-");
                }
            }

            sb.AppendLine();
        }

        private void AppendReplayRawSegments(StringBuilder sb, Entity line)
        {
            if (!EntityManager.HasBuffer<RouteSegment>(line))
                return;

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            sb.Append("routeSegments:");
            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                sb.Append(" | seg").Append(i).Append("=").Append(segmentEntity.Index);
                if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
                    continue;

                DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                sb.Append("[");
                for (int pathIndex = 0; pathIndex < path.Length; pathIndex++)
                {
                    if (pathIndex > 0)
                        sb.Append(" -> ");
                    sb.Append(path[pathIndex].m_Target.Index);
                }
                sb.Append("]");
            }
            sb.AppendLine();
        }

        private void AppendReplayOfficialSegmentStructures(
            StringBuilder sb,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                return;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            sb.Append("segmentStruct:");
            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                Entity startWaypoint = i < waypoints.Length ? waypoints[i].m_Waypoint : Entity.Null;
                Entity endWaypoint = waypoints.Length > 0 ? waypoints[(i + 1) % waypoints.Length].m_Waypoint : Entity.Null;
                sb.Append(" | seg").Append(i)
                  .Append(" ent=").Append(segmentEntity.Index)
                  .Append(" wp=").Append(startWaypoint.Index).Append("->").Append(endWaypoint.Index);

                if (startWaypoint != Entity.Null && EntityManager.HasComponent<RouteLane>(startWaypoint))
                {
                    RouteLane startRouteLane = EntityManager.GetComponentData<RouteLane>(startWaypoint);
                    sb.Append(" routeLaneStart=(")
                      .Append(FormatEntityRefCompact(startRouteLane.m_StartLane)).Append("->")
                      .Append(FormatEntityRefCompact(startRouteLane.m_EndLane)).Append(" ")
                      .Append(startRouteLane.m_StartCurvePos.ToString("0.###")).Append("->")
                      .Append(startRouteLane.m_EndCurvePos.ToString("0.###")).Append(")");
                }
                else
                {
                    sb.Append(" routeLaneStart=(-)");
                }

                if (segmentEntity != Entity.Null && EntityManager.HasComponent<PathTargets>(segmentEntity))
                {
                    PathTargets pathTargets = EntityManager.GetComponentData<PathTargets>(segmentEntity);
                    sb.Append(" pathTargets=(")
                      .Append(FormatEntityRefCompact(pathTargets.m_StartLane)).Append("->")
                      .Append(FormatEntityRefCompact(pathTargets.m_EndLane)).Append(" curve=")
                      .Append(pathTargets.m_CurvePositions.x.ToString("0.###")).Append("->")
                      .Append(pathTargets.m_CurvePositions.y.ToString("0.###"))
                      .Append(" ready=").Append(FormatFloat3Compact(pathTargets.m_ReadyStartPosition))
                      .Append("->").Append(FormatFloat3Compact(pathTargets.m_ReadyEndPosition))
                      .Append(")");
                }
                else
                {
                    sb.Append(" pathTargets=(-)");
                }

                if (segmentEntity != Entity.Null && EntityManager.HasBuffer<CurveElement>(segmentEntity))
                {
                    DynamicBuffer<CurveElement> curves = EntityManager.GetBuffer<CurveElement>(segmentEntity, true);
                    sb.Append(" curves=").Append(curves.Length);
                    if (curves.Length > 0)
                    {
                        CurveElement firstCurve = curves[0];
                        CurveElement lastCurve = curves[curves.Length - 1];
                        sb.Append(" first=").Append(FormatFloat3Compact(firstCurve.m_Curve.a))
                          .Append("->").Append(FormatFloat3Compact(firstCurve.m_Curve.d))
                          .Append(" tan=").Append(FormatFloat3Compact(math.normalizesafe(firstCurve.m_Curve.b - firstCurve.m_Curve.a)))
                          .Append("->").Append(FormatFloat3Compact(math.normalizesafe(firstCurve.m_Curve.d - firstCurve.m_Curve.c)));
                        sb.Append(" last=").Append(FormatFloat3Compact(lastCurve.m_Curve.a))
                          .Append("->").Append(FormatFloat3Compact(lastCurve.m_Curve.d))
                          .Append(" tan=").Append(FormatFloat3Compact(math.normalizesafe(lastCurve.m_Curve.b - lastCurve.m_Curve.a)))
                          .Append("->").Append(FormatFloat3Compact(math.normalizesafe(lastCurve.m_Curve.d - lastCurve.m_Curve.c)));
                    }
                }
                else
                {
                    sb.Append(" curves=0");
                }

                if (segmentEntity != Entity.Null && EntityManager.HasBuffer<CurveSource>(segmentEntity))
                {
                    DynamicBuffer<CurveSource> sources = EntityManager.GetBuffer<CurveSource>(segmentEntity, true);
                    sb.Append(" curveSources=").Append(sources.Length);
                    if (sources.Length > 0)
                    {
                        CurveSource firstSource = sources[0];
                        CurveSource lastSource = sources[sources.Length - 1];
                        sb.Append(" src0=").Append(FormatEntityRefCompact(firstSource.m_Entity))
                          .Append("@").Append(firstSource.m_Range.x.ToString("0.###")).Append("->").Append(firstSource.m_Range.y.ToString("0.###"));
                        sb.Append(" srcN=").Append(FormatEntityRefCompact(lastSource.m_Entity))
                          .Append("@").Append(lastSource.m_Range.x.ToString("0.###")).Append("->").Append(lastSource.m_Range.y.ToString("0.###"));
                    }
                }
                else
                {
                    sb.Append(" curveSources=0");
                }

                if (segmentEntity != Entity.Null && EntityManager.HasBuffer<PathElement>(segmentEntity))
                {
                    DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                    sb.Append(" path=").Append(path.Length);
                    if (path.Length > 0)
                    {
                        PathElement firstPath = path[0];
                        PathElement lastPath = path[path.Length - 1];
                        sb.Append(" p0=").Append(FormatEntityRefCompact(firstPath.m_Target))
                          .Append("@").Append(firstPath.m_TargetDelta.x.ToString("0.###")).Append("->").Append(firstPath.m_TargetDelta.y.ToString("0.###"))
                          .Append("#").Append((int)firstPath.m_Flags);
                        sb.Append(" pN=").Append(FormatEntityRefCompact(lastPath.m_Target))
                          .Append("@").Append(lastPath.m_TargetDelta.x.ToString("0.###")).Append("->").Append(lastPath.m_TargetDelta.y.ToString("0.###"))
                          .Append("#").Append((int)lastPath.m_Flags);
                    }
                }
                else
                {
                    sb.Append(" path=0");
                }
            }
            sb.AppendLine();
        }

        private static string FormatEntityRefCompact(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
        }

        private static string FormatFloat3Compact(float3 value)
        {
            return value.x.ToString("0.#") + "," + value.y.ToString("0.#") + "," + value.z.ToString("0.#");
        }

        private void AppendReplayTrackAtoms(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("trackAtoms:");
            for (int i = 0; i < chain.TrackAtoms.Count; i++)
            {
                TrackAtom atom = chain.TrackAtoms[i];
                sb.Append(" | a").Append(i)
                  .Append(" lane=").Append(atom.Key.PhysicalLaneKey.Index)
                  .Append(" prev=").Append(atom.Key.PreviousTarget.Index)
                  .Append(" next=").Append(atom.Key.NextTarget.Index)
                  .Append(" source=").Append(atom.SourceTarget.Index)
                  .Append(" flags=").Append(atom.SourceFlags)
                  .Append(" class=").Append(atom.AtomClass);
            }
            sb.AppendLine();
        }

        private void AppendReplaySegmentRanges(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("segmentRanges:");
            for (int i = 0; i < chain.SegmentRanges.Count; i++)
            {
                TrackSegmentRange range = chain.SegmentRanges[i];
                sb.Append(" | seg").Append(i)
                  .Append("=").Append(range.StartAtomIndex)
                  .Append("..").Append(range.EndAtomIndexExclusive);
            }
            sb.AppendLine();
        }

        private void AppendReplayControlEdgeSharedSpans(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("controlEdgeSharedSpans:");
            for (int i = 0; i < chain.ControlEdgeSharedSpans.Count; i++)
            {
                ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[i];
                sb.Append(" | ces").Append(i)
                  .Append(" edge=").Append(span.ControlEdgeIndex)
                  .Append(" atoms=").Append(span.StartAtomIndex).Append("..").Append(span.EndAtomIndexExclusive)
                  .Append(" sharedLines=").Append(span.SharedLineCount)
                  .Append(" mirrored=").Append(span.HasMirroredContext ? "1" : "0");
            }
            sb.AppendLine();
        }

        private void AppendReplayControlPoints(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("controlPoints:");
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker cp = chain.ControlPoints[i];
                sb.Append(" | cp").Append(i)
                  .Append(" atom=").Append(cp.AtomIndex)
                  .Append(" wp=").Append(cp.WaypointIndex)
                  .Append(" kind=").Append(cp.Kind)
                  .Append(" stop=").Append(cp.Building.Index)
                  .Append(" label=").Append(FormatTrackModelDisplayStationLabel(cp.Building, cp.WaypointIndex));
            }
            sb.AppendLine();
        }

        private void AppendReplayControlEdges(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("controlEdges:");
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                sb.Append(" | edge").Append(i)
                  .Append(" cp=").Append(edge.StartControlPointIndex).Append("->").Append(edge.EndControlPointIndex)
                  .Append(" atoms=").Append(edge.StartAtomIndex).Append("..").Append(edge.EndAtomIndexExclusive)
                  .Append(" base=").Append(FormatEtaFrames(edge.BaseFrames));
            }
            sb.AppendLine();
        }

        private void AppendReplaySharedRuns(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("sharedRuns:");
            for (int i = 0; i < chain.SharedRuns.Count; i++)
            {
                SharedTrackRun run = chain.SharedRuns[i];
                sb.Append(" | run").Append(i)
                  .Append("=").Append(run.StartAtomIndex).Append("..").Append(run.EndAtomIndexExclusive)
                  .Append(" sharedLines=").Append(run.SharedLineCount)
                  .Append(" mirrored=").Append(run.HasMirroredContext ? "1" : "0");
            }
            sb.AppendLine();
        }

        private void AppendReplaySharedRunsByOtherLine(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("sharedRunsByOtherLine:");
            if (chain.SharedRunsByOtherLine.Count == 0)
            {
                sb.Append(" none").AppendLine();
                return;
            }

            List<Entity> others = new List<Entity>(chain.SharedRunsByOtherLine.Keys);
            others.Sort((a, b) => a.Index.CompareTo(b.Index));
            for (int i = 0; i < others.Count; i++)
            {
                Entity otherLine = others[i];
                sb.Append(" | line=").Append(otherLine.Index).Append("(").Append(FormatReadableLineLabel(otherLine)).Append(")");
                List<SharedTrackRun> runs = chain.SharedRunsByOtherLine[otherLine];
                for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    SharedTrackRun run = runs[runIndex];
                    sb.Append(" run").Append(runIndex)
                      .Append("=").Append(run.StartAtomIndex).Append("..").Append(run.EndAtomIndexExclusive)
                      .Append(" mirrored=").Append(run.HasMirroredContext ? "1" : "0");
                }
            }
            sb.AppendLine();
        }

        private void AppendReplayProtectedIntervals(StringBuilder sb, LineTrackChain chain)
        {
            sb.Append("protectedIntervals:");
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval interval = chain.BypassProtectedIntervals[i];
                sb.Append(" | p").Append(i)
                  .Append(" cp=").Append(interval.StartControlPointIndex).Append("->").Append(interval.EndControlPointIndex)
                  .Append(" edges=").Append(interval.StartControlEdgeIndex).Append("..").Append(interval.EndControlEdgeIndexInclusive)
                  .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                  .Append(" base=").Append(FormatEtaFrames(interval.BaseFrames));
            }
            sb.AppendLine();

            sb.Append("protectedShared:");
            for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
            {
                ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                sb.Append(" | ps").Append(i)
                  .Append(" p=").Append(interval.ProtectedIntervalIndex)
                  .Append(" edge=").Append(interval.ControlEdgeIndex)
                  .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                  .Append(" sharedLines=").Append(interval.SharedLineCount)
                  .Append(" mirrored=").Append(interval.HasMirroredContext ? "1" : "0")
                  .Append(" entry=").Append(FormatEtaFrames(interval.EntryOffsetFrames))
                  .Append(" clear=").Append(FormatEtaFrames(interval.ClearOffsetFrames));
            }
            sb.AppendLine();

            sb.Append("protectedSummaries:");
            for (int i = 0; i < chain.ProtectedIntervalSummaries.Count; i++)
            {
                ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[i];
                sb.Append(" | p").Append(i)
                  .Append(" shared=").Append(summary.SharedSegmentCount)
                  .Append(" maxSharedLines=").Append(summary.MaxSharedLineCount)
                  .Append(" mirrored=").Append(summary.HasMirroredContext ? "1" : "0")
                  .Append(" minEntry=").Append(FormatEtaFrames(summary.MinEntryOffsetFrames))
                  .Append(" maxClear=").Append(FormatEtaFrames(summary.MaxClearOffsetFrames));
            }
            sb.AppendLine();
        }

        private int CountControlPointsInSpan(AggregatedCorridorSpan span)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(span.LineEntity))
                return 0;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(span.LineEntity, true);
            if (!m_Build.TryGetLineTrackChain(span.LineEntity, waypoints, out LineTrackChain chain))
                return 0;

            int count = 0;
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                int atomIndex = chain.ControlPoints[i].AtomIndex;
                if (atomIndex >= span.StartAtomIndex && atomIndex < span.EndAtomIndexExclusive)
                    count++;
            }

            return count;
        }

        private List<CorridorStationAnchor> BuildCorridorStationAnchors(
            List<AggregatedCorridorSpan> spans,
            AggregatedCorridorSpan reference,
            List<int> referenceSequence,
            float displayLength)
        {
            List<CorridorStationAnchor> anchors = new List<CorridorStationAnchor>();
            for (int i = 0; i < spans.Count; i++)
            {
                AggregatedCorridorSpan span = spans[i];
                if (!EntityManager.HasBuffer<RouteWaypoint>(span.LineEntity))
                    continue;

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(span.LineEntity, true);
                if (!m_Build.TryGetLineTrackChain(span.LineEntity, waypoints, out LineTrackChain chain))
                    continue;

                bool reverseOrientation = IsRunOrientationReversed(
                    referenceSequence,
                    BuildRunLaneSequence(chain, span.StartAtomIndex, span.EndAtomIndexExclusive));
                float localLength = math.max(1f, span.EndAtomIndexExclusive - span.StartAtomIndex);

                for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
                {
                    ControlPointMarker marker = chain.ControlPoints[controlPointIndex];
                    if (marker.Building == Entity.Null)
                        continue;
                    if (marker.AtomIndex < span.StartAtomIndex || marker.AtomIndex >= span.EndAtomIndexExclusive)
                        continue;

                    float localPosition = ((marker.AtomIndex - span.StartAtomIndex) / localLength) * displayLength;
                    float mappedPosition = reverseOrientation
                        ? math.max(0f, displayLength - localPosition)
                        : localPosition;
                    AddOrMergeStationAnchor(anchors, marker.Building, mappedPosition, hasStopAnchor: true);
                }

                for (int atomIndex = span.StartAtomIndex; atomIndex < span.EndAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    Entity building = m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                    if (building == Entity.Null)
                        continue;

                    float localPosition = ((atomIndex - span.StartAtomIndex) / localLength) * displayLength;
                    float mappedPosition = reverseOrientation
                        ? math.max(0f, displayLength - localPosition)
                        : localPosition;
                    AddOrMergeStationAnchor(anchors, building, mappedPosition, hasStopAnchor: false);
                }
            }

            List<CorridorStationAnchor> orderedAnchors = new List<CorridorStationAnchor>(anchors);
            orderedAnchors.Sort((a, b) => a.Position.CompareTo(b.Position));
            return orderedAnchors;
        }

        private void AppendCorridorStationAnchors(
            List<TrackModelSequenceItem> items,
            List<CorridorStationAnchor> orderedAnchors,
            float displayLength,
            string startLabel,
            string endLabel)
        {
            for (int i = 0; i < orderedAnchors.Count; i++)
            {
                string label = FormatSharedCorridorStationAnchorLabel(orderedAnchors[i]);
                float clampedPosition = math.clamp(orderedAnchors[i].Position, 0f, displayLength);
                bool nearStart = clampedPosition <= STATION_ANCHOR_MERGE_DISTANCE;
                bool nearEnd = (displayLength - clampedPosition) <= STATION_ANCHOR_MERGE_DISTANCE;
                if (string.IsNullOrEmpty(label)
                    || (nearStart && string.Equals(label, startLabel, System.StringComparison.Ordinal))
                    || (nearEnd && string.Equals(label, endLabel, System.StringComparison.Ordinal)))
                {
                    continue;
                }

                items.Add(new TrackModelSequenceItem(
                    clampedPosition,
                    0,
                    label));
            }
        }

        private static void AddOrMergeStationAnchor(
            List<CorridorStationAnchor> anchors,
            Entity building,
            float position,
            bool hasStopAnchor)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                CorridorStationAnchor existing = anchors[i];
                if (existing.Building != building)
                    continue;
                if (math.abs(existing.Position - position) > STATION_ANCHOR_MERGE_DISTANCE)
                    continue;

                anchors[i] = new CorridorStationAnchor(
                    building,
                    (existing.Position + position) * 0.5f,
                    existing.HasStopAnchor || hasStopAnchor);
                return;
            }

            anchors.Add(new CorridorStationAnchor(building, position, hasStopAnchor));
        }

        private string FormatSharedCorridorStationAnchorLabel(CorridorStationAnchor anchor)
        {
            string label = m_Diag.FormatSharedMapStationLabel(anchor.Building);
            if (string.IsNullOrEmpty(label))
                return label;

            return anchor.HasStopAnchor ? label : (label + "(过)");
        }

        private static string FormatCompactSharedMapVehicleLabel(Entity vehicle, string state)
            => FormatCompactSharedMapVehicleName(vehicle) + FormatCompactVehicleStateSuffix(state);

        private static string FormatCompactSharedMapUnknownVehicleLabel(Entity vehicle, string state)
            => FormatCompactSharedMapVehicleName(vehicle) + FormatCompactVehicleStateSuffix(state);

        private static string FormatCompactSharedMapVehicleName(Entity vehicle)
        {
            return "#" + vehicle.Index;
        }

        private static string FormatCompactVehicleStateSuffix(string state)
        {
            if (string.Equals(state, "Retiring", System.StringComparison.Ordinal))
                return "(回)";
            if (string.Equals(state, "Holding", System.StringComparison.Ordinal))
                return "(停)";
            if (string.Equals(state, "Preparing", System.StringComparison.Ordinal))
                return "(备)";
            return string.Empty;
        }

        private static bool TrySnapVehicleToNearbyStationAnchor(
            float mappedPosition,
            List<CorridorStationAnchor> anchors,
            out float snappedPosition)
        {
            snappedPosition = 0f;
            if (anchors == null || anchors.Count == 0)
                return false;

            float bestDistance = float.MaxValue;
            for (int i = 0; i < anchors.Count; i++)
            {
                float distance = math.abs(anchors[i].Position - mappedPosition);
                if (distance > STATION_VEHICLE_ATTACH_DISTANCE || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                snappedPosition = anchors[i].Position;
            }

            return bestDistance < float.MaxValue;
        }

        private string FormatSharedPhysicalCorridorAudit(SharedPhysicalCorridorAuditEntry entry)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(entry.CorridorLabel)
              .Append(" | ")
              .Append(entry.LineLabel)
              .Append(" | ")
              .Append(m_Diag.FormatSharedMapVehicleNameLabel(entry.Vehicle))
              .Append(" #")
              .Append(entry.Vehicle.Index)
              .Append(" | ")
              .Append(entry.State)
              .Append(" | included=")
              .Append(entry.Included ? "yes" : "no")
              .Append(" | reason=")
              .Append(entry.Reason);

            if (entry.AtomIndex >= 0)
            {
                sb.Append(" | atom=")
                  .Append(entry.AtomIndex)
                  .Append(" p=")
                  .Append(entry.AtomPosition01.ToString("0.00"))
                  .Append(" conf=")
                  .Append(entry.Confidence.ToString("0.00"));
            }

            return sb.ToString();
        }

        private void ResolveRunBoundaryLabels(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out string startLabel,
            out string endLabel)
        {
            startLabel = "atom" + startAtomIndex;
            endLabel = "atom" + math.max(startAtomIndex, endAtomIndexExclusive - 1);
            if (chain == null)
                return;

            if (TryResolveRunBoundaryLabelsFromAtoms(chain, startAtomIndex, endAtomIndexExclusive, out string atomStartLabel, out string atomEndLabel))
            {
                if (!string.IsNullOrEmpty(atomStartLabel))
                    startLabel = atomStartLabel;
                if (!string.IsNullOrEmpty(atomEndLabel))
                    endLabel = atomEndLabel;
            }

            if (chain.ControlPoints.Count == 0)
                return;

            int startControlPointIndex = -1;
            int endControlPointIndex = -1;
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker marker = chain.ControlPoints[i];
                if (marker.AtomIndex <= startAtomIndex)
                    startControlPointIndex = i;
                if (endControlPointIndex < 0 && marker.AtomIndex >= endAtomIndexExclusive - 1)
                    endControlPointIndex = i;
            }

            if (startControlPointIndex < 0)
                startControlPointIndex = 0;
            if (endControlPointIndex < 0)
                endControlPointIndex = chain.ControlPoints.Count - 1;

            string controlStartLabel = m_Diag.FormatSharedMapStationLabel(chain.ControlPoints[startControlPointIndex].Building);
            string controlEndLabel = m_Diag.FormatSharedMapStationLabel(chain.ControlPoints[endControlPointIndex].Building);
            if (!string.IsNullOrEmpty(controlStartLabel))
                startLabel = controlStartLabel;
            if (!string.IsNullOrEmpty(controlEndLabel))
                endLabel = controlEndLabel;
        }

        private bool TryResolveRunBoundaryLabelsFromAtoms(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out string startLabel,
            out string endLabel)
        {
            startLabel = string.Empty;
            endLabel = string.Empty;
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            for (int atomIndex = math.max(0, startAtomIndex); atomIndex < endAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                Entity building = m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                    continue;

                startLabel = m_Diag.FormatSharedMapStationLabel(building);
                if (!string.IsNullOrEmpty(startLabel))
                    break;
            }

            for (int atomIndex = math.min(chain.TrackAtoms.Count - 1, endAtomIndexExclusive - 1); atomIndex >= startAtomIndex && atomIndex >= 0; atomIndex--)
            {
                Entity building = m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                    continue;

                endLabel = m_Diag.FormatSharedMapStationLabel(building);
                if (!string.IsNullOrEmpty(endLabel))
                    break;
            }

            return !string.IsNullOrEmpty(startLabel) || !string.IsNullOrEmpty(endLabel);
        }

        private bool TryResolveSyntheticLoopClosureEndLabel(
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out string endLabel)
        {
            endLabel = string.Empty;
            if (chain == null
                || waypoints.Length < 2
                || chain.ControlPoints.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            Entity originBuilding = m_Support.GetStationBuildingForWaypoint(waypoints, 0);
            if (originBuilding == Entity.Null)
                return false;

            ControlPointMarker lastMarker = chain.ControlPoints[chain.ControlPoints.Count - 1];
            if (lastMarker.Building == Entity.Null || lastMarker.Building == originBuilding)
                return false;

            int atomsAfterLastMarker = endAtomIndexExclusive - lastMarker.AtomIndex;
            int tailAtomsOutsideSpan = chain.TrackAtoms.Count - endAtomIndexExclusive;
            if (atomsAfterLastMarker < 8)
                return false;
            if (tailAtomsOutsideSpan > 12)
                return false;
            if (lastMarker.AtomIndex <= startAtomIndex)
                return false;

            endLabel = m_Diag.FormatSharedMapStationLabel(originBuilding);
            return !string.IsNullOrEmpty(endLabel);
        }

        private static List<int> BuildRunLaneSequence(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive)
        {
            List<int> sequence = new List<int>();
            if (chain == null)
                return sequence;

            for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass == TrackAtomClass.PrimaryLane)
                    sequence.Add(atom.Key.PhysicalLaneKey.Index);
            }

            return sequence;
        }

        private static bool IsRunOrientationReversed(List<int> referenceSequence, List<int> candidateSequence)
        {
            if (referenceSequence == null
                || candidateSequence == null
                || referenceSequence.Count == 0
                || referenceSequence.Count != candidateSequence.Count)
            {
                return false;
            }

            bool same = true;
            for (int i = 0; i < referenceSequence.Count; i++)
            {
                if (referenceSequence[i] != candidateSequence[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return false;

            for (int i = 0; i < referenceSequence.Count; i++)
            {
                if (referenceSequence[i] != candidateSequence[candidateSequence.Count - 1 - i])
                    return false;
            }

            return true;
        }

        private bool TryBuildSharedCorridorDumpRow(
            Entity referenceLine,
            DynamicBuffer<RouteWaypoint> referenceWaypoints,
            LineTrackChain referenceChain,
            BypassProtectedInterval referenceInterval,
            out string dedupeKey,
            out string row)
        {
            dedupeKey = string.Empty;
            row = string.Empty;
            if (referenceChain == null)
                return false;

            Entity startBuilding = referenceChain.ControlPoints[referenceInterval.StartControlPointIndex].Building;
            Entity endBuilding = referenceChain.ControlPoints[referenceInterval.EndControlPointIndex].Building;
            if (startBuilding == Entity.Null || endBuilding == Entity.Null)
                return false;

            List<Entity> corridorLines = new List<Entity> { referenceLine };
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var allLines = m_Support.GetLineEntities(Allocator.Temp);
            try
            {
                for (int i = 0; i < allLines.Length; i++)
                {
                    Entity otherLine = allLines[i];
                    if (otherLine == Entity.Null || otherLine == referenceLine || !EntityManager.Exists(otherLine))
                        continue;
                    if (!routeWaypointBuffers.TryGetBuffer(otherLine, out DynamicBuffer<RouteWaypoint> otherWaypoints))
                        continue;
                    if (!m_Build.TryGetLineTrackChain(otherLine, otherWaypoints, out LineTrackChain otherChain))
                        continue;

                    m_Intervals.EnsureBypassPipelineReady(otherChain);
                    ProtectedIntervalMatch otherMatch = m_Shared.FindBestMatchingProtectedInterval(referenceChain, referenceInterval, otherChain);
                    if (otherMatch.Found)
                        corridorLines.Add(otherLine);
                }

                corridorLines.Sort((a, b) =>
                {
                    int cmp = string.CompareOrdinal(m_Diag.FormatSharedMapLineLabel(a), m_Diag.FormatSharedMapLineLabel(b));
                    if (cmp != 0)
                        return cmp;
                    return a.Index.CompareTo(b.Index);
                });
                StringBuilder keyBuilder = new StringBuilder();
                ulong intervalSignature = SharedIndex.ComputeProtectedIntervalAtomSignature(referenceChain, referenceInterval);
                keyBuilder.Append(intervalSignature).Append("|");
                for (int i = 0; i < corridorLines.Count; i++)
                {
                    if (i > 0)
                        keyBuilder.Append(",");
                    keyBuilder.Append(corridorLines[i].Index);
                }
                dedupeKey = keyBuilder.ToString();

                float intervalDisplayLength = TrackIntervals.GetProtectedIntervalDisplayLength(referenceInterval);

                List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(referenceWaypoints.Length + 16);
                for (int controlPointIndex = referenceInterval.StartControlPointIndex; controlPointIndex <= referenceInterval.EndControlPointIndex; controlPointIndex++)
                {
                    ControlPointMarker marker = referenceChain.ControlPoints[controlPointIndex];

                    items.Add(new TrackModelSequenceItem(
                        TrackIntervals.MapControlPointToProtectedIntervalCoordinate(referenceChain, referenceInterval, controlPointIndex),
                        0,
                        m_Diag.FormatSharedMapStationLabel(marker.Building)));
                }

                // Static corridor dumps intentionally omit live vehicle projection.

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
                sb.Append(m_Diag.FormatSharedMapStationLabel(startBuilding))
                  .Append(" -> ")
                  .Append(m_Diag.FormatSharedMapStationLabel(endBuilding))
                  .Append(" | ");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        sb.Append(" -> ");
                    sb.Append(items[i].Label);
                }

                row = sb.ToString();
                return true;
            }
            finally
            {
                allLines.Dispose();
            }
        }

        private static ulong MixLineSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
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

        private Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
            => m_Support.GetBypassBuildingForWaypoint(waypoints, waypointIndex);

        private BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData
            => m_Support.GetBufferLookup<T>(isReadOnly);

        internal void DumpTrackModelSnapshot()
        {
            if (!RtLog.DebugToolsEnabled)
                return;

            var lines = m_Support.GetLineEntities(Allocator.Temp);
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

        private bool IsAppliedLocal(Entity line) => m_Support.IsAppliedLocal(line);
        private bool IsAppliedExpress(Entity line) => m_Support.IsAppliedExpress(line);
        private string FormatReadableLineLabel(Entity line) => line == Entity.Null ? "-" : m_Diag.ResolveTrackModelLineLabel(line, includeEntityFallback: true);

        private static string SlotStr(int minute)
        {
            minute = ((minute % 1440) + 1440) % 1440;
            int h = minute / 60 % 24;
            int m = minute % 60;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

        private static string FormatEtaFrames(float frames)
        {
            if (frames == float.MaxValue)
                return "?";

            return (frames / 182.044f).ToString("0.0") + "m";
        }
    }
}
