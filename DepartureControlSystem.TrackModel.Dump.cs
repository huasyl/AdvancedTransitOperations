using System;
using System.Collections.Generic;
using System.Text;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
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
                if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
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
                if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
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

                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, ResolveWaypointSegmentIndex(chain, atomIndex)));
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
            if (!TryGetLineTrackChain(reference.LineEntity, referenceWaypoints, out LineTrackChain referenceChain))
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
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);

            for (int groupIndex = 0; groupIndex < spans.Count; groupIndex++)
            {
                AggregatedCorridorSpan corridorRef = spans[groupIndex];
                participatingLines.Add(corridorRef.LineEntity);

                if (!EntityManager.HasBuffer<RouteWaypoint>(corridorRef.LineEntity)
                    || !routeVehicleBuffers.TryGetBuffer(corridorRef.LineEntity, out DynamicBuffer<RouteVehicle> vehicles))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(corridorRef.LineEntity, true);
                if (!TryGetLineTrackChain(corridorRef.LineEntity, waypoints, out LineTrackChain chain))
                    continue;

                bool reverseOrientation = IsRunOrientationReversed(referenceSequence, BuildRunLaneSequence(chain, corridorRef.StartAtomIndex, corridorRef.EndAtomIndexExclusive));
                for (int rvIndex = 0; rvIndex < vehicles.Length; rvIndex++)
                {
                    Entity vehicle = vehicles[rvIndex].m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                        continue;

                    string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                        ? vehicleState.ToString()
                        : "Unknown";
                    bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                        && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;

                    if (!TryProjectVehicleTrackCursor(vehicle, corridorRef.LineEntity, waypoints, out VehicleTrackCursor cursor))
                    {
                        if (boarding && TryGetBoardingStationAnchorPosition(vehicle, waypoints, stationAnchors, out float boardingStationPosition))
                        {
                            auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                                corridorLabel,
                                FormatReadableLineLabel(corridorRef.LineEntity),
                                vehicle,
                                state,
                                true,
                                "boarding-station",
                                -1,
                                -1f,
                                1f)));
                            items.Add(new TrackModelSequenceItem(
                                boardingStationPosition,
                                1,
                                FormatCompactSharedMapVehicleLabel(vehicle, state)));
                            continue;
                        }

                        auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                            corridorLabel,
                            FormatReadableLineLabel(corridorRef.LineEntity),
                            vehicle,
                            state,
                            false,
                            "projection-failed",
                            -1,
                            -1f,
                            0f)));
                        continue;
                    }
                    if (cursor.AtomCursorIndex < corridorRef.StartAtomIndex)
                    {
                        if (boarding && TryGetBoardingStationAnchorPosition(vehicle, waypoints, stationAnchors, out float boardingStationPosition))
                        {
                            auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                                corridorLabel,
                                FormatReadableLineLabel(corridorRef.LineEntity),
                                vehicle,
                                state,
                                true,
                                "boarding-station",
                                cursor.AtomCursorIndex,
                                cursor.AtomPosition01,
                                cursor.Confidence)));
                            items.Add(new TrackModelSequenceItem(
                                boardingStationPosition,
                                1,
                                FormatCompactSharedMapVehicleLabel(vehicle, state)));
                            continue;
                        }

                        auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                            corridorLabel,
                            FormatReadableLineLabel(corridorRef.LineEntity),
                            vehicle,
                            state,
                            false,
                            "outside-run-before",
                            cursor.AtomCursorIndex,
                            cursor.AtomPosition01,
                            cursor.Confidence)));
                        continue;
                    }
                    if (cursor.AtomCursorIndex >= corridorRef.EndAtomIndexExclusive)
                    {
                        if (boarding && TryGetBoardingStationAnchorPosition(vehicle, waypoints, stationAnchors, out float boardingStationPosition))
                        {
                            auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                                corridorLabel,
                                FormatReadableLineLabel(corridorRef.LineEntity),
                                vehicle,
                                state,
                                true,
                                "boarding-station",
                                cursor.AtomCursorIndex,
                                cursor.AtomPosition01,
                                cursor.Confidence)));
                            items.Add(new TrackModelSequenceItem(
                                boardingStationPosition,
                                1,
                                FormatCompactSharedMapVehicleLabel(vehicle, state)));
                            continue;
                        }

                        auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                            corridorLabel,
                            FormatReadableLineLabel(corridorRef.LineEntity),
                            vehicle,
                            state,
                            false,
                            "outside-run-after",
                            cursor.AtomCursorIndex,
                            cursor.AtomPosition01,
                            cursor.Confidence)));
                        continue;
                    }

                    float localPosition = (cursor.AtomCursorIndex - corridorRef.StartAtomIndex) + math.saturate(cursor.AtomPosition01);
                    float mappedPosition = reverseOrientation
                        ? math.max(0f, displayLength - localPosition)
                        : localPosition;
                    if (boarding && TryGetBoardingStationAnchorPosition(vehicle, waypoints, stationAnchors, out float boardingInsidePosition))
                        mappedPosition = boardingInsidePosition;
                    else if (TrySnapVehicleToNearbyStationAnchor(mappedPosition, stationAnchors, out float snappedStationPosition))
                        mappedPosition = snappedStationPosition;
                    auditRows.Add(FormatSharedPhysicalCorridorAudit(new SharedPhysicalCorridorAuditEntry(
                        corridorLabel,
                        FormatReadableLineLabel(corridorRef.LineEntity),
                        vehicle,
                        state,
                        true,
                        cursor.Confidence >= 0.6f ? "inside-run" : "low-confidence",
                        cursor.AtomCursorIndex,
                        cursor.AtomPosition01,
                        cursor.Confidence)));
                    string label = cursor.Confidence >= 0.6f
                        ? FormatCompactSharedMapVehicleLabel(vehicle, state)
                        : FormatCompactSharedMapUnknownVehicleLabel(vehicle, state);
                    items.Add(new TrackModelSequenceItem(
                        cursor.Confidence >= 0.6f ? mappedPosition : float.MaxValue,
                        cursor.Confidence >= 0.6f ? 1 : 2,
                        label));
                }
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

        private int CountControlPointsInSpan(AggregatedCorridorSpan span)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(span.LineEntity))
                return 0;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(span.LineEntity, true);
            if (!TryGetLineTrackChain(span.LineEntity, waypoints, out LineTrackChain chain))
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
                if (!TryGetLineTrackChain(span.LineEntity, waypoints, out LineTrackChain chain))
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
                    Entity building = ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
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
            string label = FormatSharedMapStationLabel(anchor.Building);
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

        private bool TryGetBoardingStationAnchorPosition(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            List<CorridorStationAnchor> anchors,
            out float position)
        {
            position = 0f;
            if (vehicle == Entity.Null || anchors == null || anchors.Count == 0)
                return false;

            int waypointIndex = ComputeWpIndex(vehicle, waypoints);
            if (waypointIndex < 0)
                return false;

            Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
            if (building == Entity.Null)
                return false;

            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i].Building != building)
                    continue;

                position = anchors[i].Position;
                return true;
            }

            return false;
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
              .Append(FormatSharedMapVehicleNameLabel(entry.Vehicle))
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

            string controlStartLabel = FormatSharedMapStationLabel(chain.ControlPoints[startControlPointIndex].Building);
            string controlEndLabel = FormatSharedMapStationLabel(chain.ControlPoints[endControlPointIndex].Building);
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
                Entity building = ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                    continue;

                startLabel = FormatSharedMapStationLabel(building);
                if (!string.IsNullOrEmpty(startLabel))
                    break;
            }

            for (int atomIndex = math.min(chain.TrackAtoms.Count - 1, endAtomIndexExclusive - 1); atomIndex >= startAtomIndex && atomIndex >= 0; atomIndex--)
            {
                Entity building = ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                    continue;

                endLabel = FormatSharedMapStationLabel(building);
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

            Entity originBuilding = GetStationBuildingForWaypoint(waypoints, 0);
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

            endLabel = FormatSharedMapStationLabel(originBuilding);
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
    }
}
