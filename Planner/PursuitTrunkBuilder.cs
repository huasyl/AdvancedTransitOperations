using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class PursuitTrunkBuilder
    {
        public List<PursuitTrunk> Build(PlannerContext context)
        {
            List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> rawCorridors = BuildRawCorridors(context);
            Dictionary<string, List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>> groupedByPair =
                new Dictionary<string, List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>>(StringComparer.Ordinal);

            foreach (DepartureControlSystem.DispatchPlannerSharedCorridorDto corridor in rawCorridors)
            {
                string pairKey = corridor.lineId + "|" + corridor.otherLineId;
                if (!groupedByPair.TryGetValue(pairKey, out List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> list))
                {
                    list = new List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>();
                    groupedByPair[pairKey] = list;
                }
                list.Add(corridor);
            }

            List<PursuitTrunk> result = new List<PursuitTrunk>();
            foreach (KeyValuePair<string, List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>> entry in groupedByPair)
            {
                List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> sorted = entry.Value
                    .OrderBy(corridor => corridor.lineStartAtomIndex)
                    .ThenBy(corridor => corridor.otherStartAtomIndex)
                    .ToList();
                List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> currentGroup = new List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>();

                for (int index = 0; index < sorted.Count; index++)
                {
                    DepartureControlSystem.DispatchPlannerSharedCorridorDto corridor = sorted[index];
                    bool startsNewGroup = currentGroup.Count == 0;
                    if (!startsNewGroup)
                    {
                        DepartureControlSystem.DispatchPlannerSharedCorridorDto previous = currentGroup[currentGroup.Count - 1];
                        startsNewGroup =
                            corridor.lineStartAtomIndex > previous.lineEndAtomIndexExclusive + PlannerDefaults.PursuitTrunkMergeGapAtoms
                            || corridor.otherStartAtomIndex > previous.otherEndAtomIndexExclusive + PlannerDefaults.PursuitTrunkMergeGapAtoms;
                    }

                    if (startsNewGroup && currentGroup.Count > 0)
                    {
                        result.Add(BuildGroupTrunk(currentGroup, result.Count));
                        currentGroup.Clear();
                    }

                    currentGroup.Add(corridor);
                }

                if (currentGroup.Count > 0)
                {
                    result.Add(BuildGroupTrunk(currentGroup, result.Count));
                }
            }

            return result;
        }

        private static List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> BuildRawCorridors(PlannerContext context)
        {
            List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> result =
                new List<DepartureControlSystem.DispatchPlannerSharedCorridorDto>();
            HashSet<string> selectedLineSet = new HashSet<string>(context.SelectedLineIds ?? new string[0], StringComparer.Ordinal);
            HashSet<string> selectedExpressLineSet = new HashSet<string>(context.SelectedExpressLineIds ?? new string[0], StringComparer.Ordinal);
            HashSet<string> adjustableLineSet = new HashSet<string>(context.AdjustableLineIds ?? new string[0], StringComparer.Ordinal);

            foreach (DepartureControlSystem.DispatchPlannerSharedCorridorDto corridor in context.Snapshot.currentTrackScenario?.sharedCorridors ?? new DepartureControlSystem.DispatchPlannerSharedCorridorDto[0])
            {
                if (corridor == null
                    || !string.Equals(corridor.traversalRelation, "SameDirection", StringComparison.OrdinalIgnoreCase)
                    || corridor.hasMirroredContext
                    || corridor.orderedRun <= 0
                    || corridor.physicalOverlap <= 0)
                {
                    continue;
                }

                if (string.Equals(context.ExpressSourceMode, "existing", StringComparison.OrdinalIgnoreCase))
                {
                    if (!selectedLineSet.Contains(corridor.lineId ?? string.Empty)
                        || !selectedLineSet.Contains(corridor.otherLineId ?? string.Empty))
                    {
                        continue;
                    }
                    if (selectedExpressLineSet.Count > 0
                        && !selectedExpressLineSet.Contains(corridor.lineId ?? string.Empty)
                        && !selectedExpressLineSet.Contains(corridor.otherLineId ?? string.Empty))
                    {
                        continue;
                    }
                    if (selectedExpressLineSet.Count > 0
                        && adjustableLineSet.Count > 0
                        && !IsTargetExpressToAdjustableCorridor(corridor, selectedExpressLineSet, adjustableLineSet))
                    {
                        continue;
                    }

                    result.Add(OrientCorridorForRoles(context, corridor));
                }
                else
                {
                    string baseLineId = context.VirtualExpressBaseLineId;
                    if (string.IsNullOrEmpty(baseLineId))
                    {
                        continue;
                    }

                    if (string.Equals(corridor.otherLineId, baseLineId, StringComparison.Ordinal))
                    {
                        DepartureControlSystem.DispatchPlannerSharedCorridorDto mapped = CloneCorridor(corridor);
                        mapped.otherLineId = context.VirtualExpressLineId;
                        mapped.id = (corridor.id ?? string.Empty).Replace(baseLineId, context.VirtualExpressLineId);
                        result.Add(OrientCorridorForRoles(context, mapped));
                    }
                }
            }

            if (string.Equals(context.ExpressSourceMode, "virtual", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(context.VirtualExpressBaseLineId)
                && selectedLineSet.Contains(context.VirtualExpressBaseLineId)
                && context.LineTracksByLineId.TryGetValue(context.VirtualExpressBaseLineId, out DepartureControlSystem.DispatchPlannerLineTrackDto lineTrack)
                && context.StationsByLineId.TryGetValue(context.VirtualExpressBaseLineId, out List<DepartureControlSystem.DispatchPlannerStationDto> stations)
                && stations.Count > 0)
            {
                DepartureControlSystem.DispatchPlannerSharedCorridorDto fullCorridor = new DepartureControlSystem.DispatchPlannerSharedCorridorDto();
                fullCorridor.id = context.VirtualExpressBaseLineId + "|" + context.VirtualExpressLineId + "|full";
                fullCorridor.lineId = context.VirtualExpressBaseLineId;
                fullCorridor.otherLineId = context.VirtualExpressLineId;
                fullCorridor.lineStartAtomIndex = 0;
                fullCorridor.lineEndAtomIndexExclusive = lineTrack.trackAtomCount;
                fullCorridor.otherStartAtomIndex = 0;
                fullCorridor.otherEndAtomIndexExclusive = lineTrack.trackAtomCount;
                fullCorridor.lineStartStationId = stations[0].id;
                fullCorridor.lineEndStationId = stations[stations.Count - 1].id;
                fullCorridor.otherStartStationId = context.VirtualExpressLineId + ":station-0";
                fullCorridor.otherEndStationId = context.VirtualExpressLineId + ":station-" + (stations.Count - 1);
                fullCorridor.physicalOverlap = lineTrack.trackAtomCount;
                fullCorridor.orderedRun = lineTrack.trackAtomCount;
                fullCorridor.confidence = 0.9f;
                result.Add(OrientCorridorForRoles(context, fullCorridor));
            }

            return result;
        }

        private static bool IsTargetExpressToAdjustableCorridor(
            DepartureControlSystem.DispatchPlannerSharedCorridorDto corridor,
            HashSet<string> selectedExpressLineSet,
            HashSet<string> adjustableLineSet)
        {
            string lineId = corridor.lineId ?? string.Empty;
            string otherLineId = corridor.otherLineId ?? string.Empty;
            return (selectedExpressLineSet.Contains(lineId) && adjustableLineSet.Contains(otherLineId))
                || (selectedExpressLineSet.Contains(otherLineId) && adjustableLineSet.Contains(lineId));
        }

        private static DepartureControlSystem.DispatchPlannerSharedCorridorDto OrientCorridorForRoles(
            PlannerContext context,
            DepartureControlSystem.DispatchPlannerSharedCorridorDto corridor)
        {
            string primaryLineId = corridor.lineId ?? string.Empty;
            string secondaryLineId = corridor.otherLineId ?? string.Empty;
            bool primaryAdjustable = (context.AdjustableLineIds ?? new string[0]).Contains(primaryLineId);
            bool secondaryAdjustable = (context.AdjustableLineIds ?? new string[0]).Contains(secondaryLineId);
            bool usePrimaryAsYielding;
            if (primaryAdjustable != secondaryAdjustable)
            {
                usePrimaryAsYielding = primaryAdjustable;
            }
            else
            {
                string primaryKind = context.LinesById.TryGetValue(primaryLineId, out DepartureControlSystem.DispatchPlannerLineDto primaryLine)
                    ? primaryLine.kind ?? "local"
                    : string.Equals(primaryLineId, context.VirtualExpressLineId, StringComparison.Ordinal) ? "express" : "local";
                string secondaryKind = context.LinesById.TryGetValue(secondaryLineId, out DepartureControlSystem.DispatchPlannerLineDto secondaryLine)
                    ? secondaryLine.kind ?? "local"
                    : string.Equals(secondaryLineId, context.VirtualExpressLineId, StringComparison.Ordinal) ? "express" : "local";
                usePrimaryAsYielding = string.Equals(primaryKind, secondaryKind, StringComparison.OrdinalIgnoreCase)
                    ? string.Compare(primaryLineId, secondaryLineId, StringComparison.Ordinal) <= 0
                    : !string.Equals(primaryKind, "express", StringComparison.OrdinalIgnoreCase);
            }

            return usePrimaryAsYielding ? CloneCorridor(corridor) : SwapCorridorSides(corridor);
        }

        private static DepartureControlSystem.DispatchPlannerSharedCorridorDto SwapCorridorSides(DepartureControlSystem.DispatchPlannerSharedCorridorDto source)
        {
            return new DepartureControlSystem.DispatchPlannerSharedCorridorDto
            {
                id = source.id,
                lineId = source.otherLineId,
                otherLineId = source.lineId,
                traversalRelation = source.traversalRelation,
                lineStartAtomIndex = source.otherStartAtomIndex,
                lineEndAtomIndexExclusive = source.otherEndAtomIndexExclusive,
                otherStartAtomIndex = source.lineStartAtomIndex,
                otherEndAtomIndexExclusive = source.lineEndAtomIndexExclusive,
                lineStartStationId = source.otherStartStationId,
                lineEndStationId = source.otherEndStationId,
                otherStartStationId = source.lineStartStationId,
                otherEndStationId = source.lineEndStationId,
                orderedRun = source.orderedRun,
                physicalOverlap = source.physicalOverlap,
                confidence = source.confidence,
                hasMirroredContext = source.hasMirroredContext,
                hasCanonicalDirection = source.hasCanonicalDirection
            };
        }

        private static DepartureControlSystem.DispatchPlannerSharedCorridorDto CloneCorridor(DepartureControlSystem.DispatchPlannerSharedCorridorDto source)
        {
            return new DepartureControlSystem.DispatchPlannerSharedCorridorDto
            {
                id = source.id,
                lineId = source.lineId,
                otherLineId = source.otherLineId,
                traversalRelation = source.traversalRelation,
                lineStartAtomIndex = source.lineStartAtomIndex,
                lineEndAtomIndexExclusive = source.lineEndAtomIndexExclusive,
                otherStartAtomIndex = source.otherStartAtomIndex,
                otherEndAtomIndexExclusive = source.otherEndAtomIndexExclusive,
                lineStartStationId = source.lineStartStationId,
                lineEndStationId = source.lineEndStationId,
                otherStartStationId = source.otherStartStationId,
                otherEndStationId = source.otherEndStationId,
                orderedRun = source.orderedRun,
                physicalOverlap = source.physicalOverlap,
                confidence = source.confidence,
                hasMirroredContext = source.hasMirroredContext,
                hasCanonicalDirection = source.hasCanonicalDirection
            };
        }

        private static PursuitTrunk BuildGroupTrunk(
            List<DepartureControlSystem.DispatchPlannerSharedCorridorDto> group,
            int groupIndex)
        {
            DepartureControlSystem.DispatchPlannerSharedCorridorDto first = group[0];
            DepartureControlSystem.DispatchPlannerSharedCorridorDto localStart = group.OrderBy(corridor => corridor.lineStartAtomIndex).First();
            DepartureControlSystem.DispatchPlannerSharedCorridorDto localEnd = group.OrderByDescending(corridor => corridor.lineEndAtomIndexExclusive).First();
            DepartureControlSystem.DispatchPlannerSharedCorridorDto expressStart = group.OrderBy(corridor => corridor.otherStartAtomIndex).First();
            DepartureControlSystem.DispatchPlannerSharedCorridorDto expressEnd = group.OrderByDescending(corridor => corridor.otherEndAtomIndexExclusive).First();

            PursuitTrunk trunk = new PursuitTrunk();
            trunk.TrunkId = first.lineId + "|" + first.otherLineId + "|trunk-group-" + groupIndex;
            trunk.LocalLineId = first.lineId ?? string.Empty;
            trunk.ExpressLineId = first.otherLineId ?? string.Empty;
            trunk.YieldingLineId = trunk.LocalLineId;
            trunk.PriorityLineId = trunk.ExpressLineId;
            trunk.FromStationId = localStart.lineStartStationId ?? string.Empty;
            trunk.ToStationId = localEnd.lineEndStationId ?? string.Empty;
            trunk.ExpressFromStationId = expressStart.otherStartStationId ?? string.Empty;
            trunk.ExpressToStationId = expressEnd.otherEndStationId ?? string.Empty;
            trunk.LocalStartAtomIndex = group.Min(corridor => corridor.lineStartAtomIndex);
            trunk.LocalEndAtomIndexExclusive = group.Max(corridor => corridor.lineEndAtomIndexExclusive);
            trunk.ExpressStartAtomIndex = group.Min(corridor => corridor.otherStartAtomIndex);
            trunk.ExpressEndAtomIndexExclusive = group.Max(corridor => corridor.otherEndAtomIndexExclusive);
            trunk.SourceCorridorCount = group.Count;
            trunk.SourceCorridorIds.AddRange(group.Select(corridor => corridor.id ?? string.Empty));
            float confidenceSum = 0f;
            for (int i = 0; i < group.Count; i++)
            {
                confidenceSum += group[i].confidence;
            }
            trunk.Confidence = group.Count > 0 ? confidenceSum / group.Count : 0.3f;
            trunk.AxisSampleCount = EstimateAxisSampleCount(trunk);
            return trunk;
        }

        private static int EstimateAxisSampleCount(PursuitTrunk trunk)
        {
            int localLength = Math.Max(1, trunk.LocalEndAtomIndexExclusive - trunk.LocalStartAtomIndex);
            int expressLength = Math.Max(1, trunk.ExpressEndAtomIndexExclusive - trunk.ExpressStartAtomIndex);
            int overlap = Math.Max(1, Math.Min(localLength, expressLength));
            return Math.Max(2, Math.Min(Math.Max(localLength, expressLength), overlap));
        }
    }
}
