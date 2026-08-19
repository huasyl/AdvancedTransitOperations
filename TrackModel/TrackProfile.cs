using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackProfile
    {
        private const int TURNBACK_REPEAT_MIN_PRIMARY_ATOMS = 3;
        private const int TURNBACK_REPEAT_MIN_UNIQUE_LANES = 2;
        private const int TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP = 2;
        private readonly TrackSupport m_Support;
        private readonly TramStopIndex m_TramStops;
        private readonly Dictionary<Entity, string> m_TrackModelTurnbackBuildLogCache = new Dictionary<Entity, string>();

        internal TrackProfile(TrackSupport support, TramStopIndex tramStops)
        {
            m_Support = support;
            m_TramStops = tramStops ?? throw new ArgumentNullException(nameof(tramStops));
        }

        private EntityManager EntityManager => m_Support.EntityManager;
        private TimedLogger log => m_Support.Log;

        internal void RegisterTramLine(
            Entity line,
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (TransportModeResolver.Resolve(EntityManager, line) == TransitMode.Tram)
                m_TramStops.RegisterLine(line, chain, waypoints);
        }

        private readonly struct StationPassRange
        {
            public readonly Entity Building;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly int WaypointIndex;
            public readonly float StopFrames;
            public readonly int PassIndex;
            public readonly string StationId;
            public readonly bool IsBreak;

            public StationPassRange(
                Entity building,
                int startAtomIndex,
                int endAtomIndexExclusive,
                int waypointIndex,
                float stopFrames,
                int passIndex,
                string stationId = "",
                bool isBreak = false)
            {
                Building = building;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                WaypointIndex = waypointIndex;
                StopFrames = stopFrames;
                PassIndex = passIndex;
                StationId = stationId ?? string.Empty;
                IsBreak = isBreak;
            }
        }

        internal void BuildTraversalProfile(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null)
                return;

            chain.TraversalProfile.Events.Clear();
            chain.TraversalProfile.RunSlices.Clear();
            chain.TraversalProfile.AtomToRunSliceIndex = Array.Empty<int>();
            chain.TraversalProfile.SegmentSliceCutPointProgresses = Array.Empty<float[]>();
            if (chain.TrackAtoms.Count == 0)
                return;

            bool hasTimeProfile = m_Support.TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader timeProfile);
            float lineFrames = m_Support.GetLineLoopFramesEstimate(line, waypoints);
            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints, true);
            if (stationPasses.Count == 0)
                return;

            Dictionary<int, int> boundaryEventIndexByAtom = new Dictionary<int, int>();
            List<int> boundaries = new List<int> { 0, chain.TrackAtoms.Count };

            for (int passIndex = 0; passIndex < stationPasses.Count; passIndex++)
            {
                StationPassRange pass = stationPasses[passIndex];
                if (!boundaries.Contains(pass.StartAtomIndex))
                    boundaries.Add(pass.StartAtomIndex);
                if (!boundaries.Contains(pass.EndAtomIndexExclusive))
                    boundaries.Add(pass.EndAtomIndexExclusive);

                if (pass.IsBreak)
                {
                    int breakIndex = chain.TraversalProfile.Events.Count;
                    chain.TraversalProfile.Events.Add(new TraversalEvent(
                        breakIndex,
                        TraversalEventKind.BreakBoundary,
                        Entity.Null,
                        -1,
                        -1,
                        pass.StartAtomIndex,
                        pass.StartAtomIndex,
                        0f));
                    boundaryEventIndexByAtom[pass.StartAtomIndex] = breakIndex;
                    continue;
                }

                bool terminal = IsTerminalRange(
                    chain,
                    pass.StartAtomIndex,
                    pass.EndAtomIndexExclusive,
                    pass.WaypointIndex);
                int boundaryWaypointIndex = terminal ? -1 : pass.WaypointIndex;
                int approachIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    approachIndex,
                    TraversalEventKind.ApproachSplitBoundary,
                    pass.Building,
                    boundaryWaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.StartAtomIndex,
                    0f));
                boundaryEventIndexByAtom[pass.StartAtomIndex] = approachIndex;

                int stationEventIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    stationEventIndex,
                    pass.WaypointIndex >= 0 && !terminal
                        ? TraversalEventKind.Stop
                        : TraversalEventKind.Pass,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.EndAtomIndexExclusive,
                    terminal ? 0f : pass.StopFrames,
                    pass.StationId));

                int departureIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    departureIndex,
                    TraversalEventKind.DepartureSplitBoundary,
                    pass.Building,
                    boundaryWaypointIndex,
                    pass.PassIndex,
                    pass.EndAtomIndexExclusive,
                    pass.EndAtomIndexExclusive,
                    0f));
                boundaryEventIndexByAtom[pass.EndAtomIndexExclusive] = departureIndex;
            }

            for (int endpointIndex = 0; endpointIndex < chain.EndpointMarkers.Count; endpointIndex++)
            {
                EndpointMarker endpoint = chain.EndpointMarkers[endpointIndex];
                if (endpoint.Kind != RouteWaypointEndpointKind.OutsideTrainConnection
                    || endpoint.AtomIndex < 0
                    || endpoint.AtomIndex > chain.TrackAtoms.Count)
                {
                    continue;
                }

                if (!boundaries.Contains(endpoint.AtomIndex))
                    boundaries.Add(endpoint.AtomIndex);

                int endpointEventIndex = chain.TraversalProfile.Events.Count;
                Entity endpointEntity = endpoint.OutsideConnection != Entity.Null
                    ? endpoint.OutsideConnection
                    : endpoint.Waypoint;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    endpointEventIndex,
                    TraversalEventKind.OutsideEndpointBoundary,
                    endpointEntity,
                    endpoint.WaypointIndex,
                    -1,
                    endpoint.AtomIndex,
                    endpoint.AtomIndex,
                    0f));
                boundaryEventIndexByAtom[endpoint.AtomIndex] = endpointEventIndex;
            }

            boundaries.Sort();
            for (int boundaryIndex = 0; boundaryIndex < boundaries.Count - 1; boundaryIndex++)
            {
                int startAtomIndex = boundaries[boundaryIndex];
                int endAtomIndexExclusive = boundaries[boundaryIndex + 1];
                if (endAtomIndexExclusive <= startAtomIndex)
                    continue;

                int sliceIndex = chain.TraversalProfile.RunSlices.Count;
                boundaryEventIndexByAtom.TryGetValue(startAtomIndex, out int startEventIndex);
                boundaryEventIndexByAtom.TryGetValue(endAtomIndexExclusive, out int endEventIndex);
                chain.TraversalProfile.RunSlices.Add(new TraversalRunSlice(
                    sliceIndex,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    startEventIndex,
                    endEventIndex,
                    CollectTraversalSlicePhysicalLaneKeys(chain, startAtomIndex, endAtomIndexExclusive),
                    EstimateTraversalRunSliceFrames(
                        chain,
                        startAtomIndex,
                        endAtomIndexExclusive,
                        hasTimeProfile,
                        timeProfile,
                        lineFrames)));
            }

            int[] atomToRunSliceIndex = new int[chain.TrackAtoms.Count];
            for (int atomIndex = 0; atomIndex < atomToRunSliceIndex.Length; atomIndex++)
                atomToRunSliceIndex[atomIndex] = -1;
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                int startAtomIndex = math.clamp(slice.StartAtomIndex, 0, atomToRunSliceIndex.Length);
                int endAtomIndexExclusive = math.clamp(slice.EndAtomIndexExclusive, startAtomIndex, atomToRunSliceIndex.Length);
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                    atomToRunSliceIndex[atomIndex] = sliceIndex;
            }

            chain.TraversalProfile.AtomToRunSliceIndex = atomToRunSliceIndex;

            List<float>[] segmentCutPoints = new List<float>[chain.SegmentRanges.Count];
            void AddSegmentCutPoint(int boundaryAtomIndex)
            {
                if (boundaryAtomIndex < 0 || boundaryAtomIndex > chain.TrackAtoms.Count)
                    return;

                for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
                {
                    TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                    int segmentStartAtomIndex = segmentRange.StartAtomIndex;
                    int segmentEndAtomExclusive = segmentRange.EndAtomIndexExclusive;
                    if (segmentEndAtomExclusive <= segmentStartAtomIndex)
                        continue;

                    bool insideSegment = boundaryAtomIndex > segmentStartAtomIndex && boundaryAtomIndex < segmentEndAtomExclusive;
                    bool atSegmentStart = boundaryAtomIndex == segmentStartAtomIndex;
                    bool atSegmentEnd = boundaryAtomIndex == segmentEndAtomExclusive;
                    if (!insideSegment && !atSegmentStart && !atSegmentEnd)
                        continue;

                    float segmentLength = math.max(1f, segmentEndAtomExclusive - segmentStartAtomIndex);
                    float progress = math.saturate((boundaryAtomIndex - segmentStartAtomIndex) / (float)segmentLength);
                    segmentCutPoints[segmentIndex] ??= new List<float>();

                    bool duplicate = false;
                    for (int progressIndex = 0; progressIndex < segmentCutPoints[segmentIndex].Count; progressIndex++)
                    {
                        if (math.abs(segmentCutPoints[segmentIndex][progressIndex] - progress) <= 0.01f)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        segmentCutPoints[segmentIndex].Add(progress);
                }
            }

            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                AddSegmentCutPoint(slice.StartAtomIndex);
                AddSegmentCutPoint(slice.EndAtomIndexExclusive);
            }

            float[][] segmentSliceCutPointProgresses = new float[chain.SegmentRanges.Count][];
            for (int segmentIndex = 0; segmentIndex < segmentCutPoints.Length; segmentIndex++)
            {
                if (segmentCutPoints[segmentIndex] == null || segmentCutPoints[segmentIndex].Count == 0)
                {
                    segmentSliceCutPointProgresses[segmentIndex] = Array.Empty<float>();
                    continue;
                }

                segmentCutPoints[segmentIndex].Sort();
                segmentSliceCutPointProgresses[segmentIndex] = segmentCutPoints[segmentIndex].ToArray();
            }

            chain.TraversalProfile.SegmentSliceCutPointProgresses = segmentSliceCutPointProgresses;
        }

        private static bool HasOppositeTraversalDirection(TrackAtom previousAtom, TrackAtom currentAtom)
        {
            return previousAtom.TraversalDir != TrackTraversalDir.Unknown
                && currentAtom.TraversalDir != TrackTraversalDir.Unknown
                && previousAtom.TraversalDir != currentAtom.TraversalDir;
        }

        private static bool HasMirroredTraversalContext(TrackAtom previousAtom, TrackAtom currentAtom)
        {
            return previousAtom.Key.PreviousTarget != Entity.Null
                && previousAtom.Key.NextTarget != Entity.Null
                && currentAtom.Key.PreviousTarget != Entity.Null
                && currentAtom.Key.NextTarget != Entity.Null
                && previousAtom.Key.PreviousTarget == currentAtom.Key.NextTarget
                && previousAtom.Key.NextTarget == currentAtom.Key.PreviousTarget;
        }

        private static bool TryMatchReverseRepeatedPrimaryAtom(
            TrackAtom previousAtom,
            TrackAtom currentAtom,
            out bool strongContextMatch,
            out bool oppositeDirectionMatch)
        {
            strongContextMatch = false;
            oppositeDirectionMatch = false;

            if (previousAtom.AtomClass != TrackAtomClass.PrimaryLane
                || currentAtom.AtomClass != TrackAtomClass.PrimaryLane
                || previousAtom.Key.PhysicalLaneKey == Entity.Null
                || previousAtom.Key.PhysicalLaneKey != currentAtom.Key.PhysicalLaneKey)
            {
                return false;
            }

            strongContextMatch = HasMirroredTraversalContext(previousAtom, currentAtom);
            oppositeDirectionMatch = HasOppositeTraversalDirection(previousAtom, currentAtom);
            return strongContextMatch || oppositeDirectionMatch;
        }

        private bool TryMeasureReverseRepeatedPrimaryRun(
            LineTrackChain chain,
            List<int> primaryAtomIndices,
            int previousPrimaryPosition,
            int currentPrimaryPosition,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || primaryAtomIndices == null
                || previousPrimaryPosition < 0
                || currentPrimaryPosition < 0
                || previousPrimaryPosition >= primaryAtomIndices.Count
                || currentPrimaryPosition >= primaryAtomIndices.Count)
            {
                return false;
            }

            int strongContextMatchCount = 0;
            int oppositeDirectionMatchCount = 0;
            HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
            int leftPosition = previousPrimaryPosition;
            int rightPosition = currentPrimaryPosition;
            while (leftPosition >= 0 && rightPosition < primaryAtomIndices.Count)
            {
                TrackAtom previousAtom = chain.TrackAtoms[primaryAtomIndices[leftPosition]];
                TrackAtom currentAtom = chain.TrackAtoms[primaryAtomIndices[rightPosition]];
                if (!TryMatchReverseRepeatedPrimaryAtom(
                        previousAtom,
                        currentAtom,
                        out bool strongContextMatch,
                        out bool oppositeDirectionMatch))
                {
                    break;
                }

                matchedAtomCount++;
                if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                    matchedUniqueLaneCount++;
                if (strongContextMatch)
                    strongContextMatchCount++;
                if (oppositeDirectionMatch)
                    oppositeDirectionMatchCount++;

                leftPosition--;
                rightPosition++;
            }

            return matchedAtomCount >= TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                && matchedUniqueLaneCount >= TURNBACK_REPEAT_MIN_UNIQUE_LANES
                && (strongContextMatchCount > 0 || oppositeDirectionMatchCount >= 2);
        }

        private static List<int> CollectPrimaryAtomIndices(LineTrackChain chain)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return primaryAtomIndices;

            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private static List<int> CollectPrimaryAtomIndicesForRange(
            LineTrackChain chain,
            TrackSegmentRange range)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || range.EndAtomIndexExclusive <= range.StartAtomIndex)
            {
                return primaryAtomIndices;
            }

            int startAtomIndex = math.clamp(range.StartAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int endAtomIndexExclusive = math.clamp(range.EndAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);
            for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private bool TryMeasureAdjacentSegmentReverseOverlap(
            LineTrackChain chain,
            int leftSegmentIndex,
            int rightSegmentIndex,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || leftSegmentIndex < 0
                || rightSegmentIndex <= leftSegmentIndex
                || rightSegmentIndex >= chain.SegmentRanges.Count)
            {
                return false;
            }

            List<int> leftPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[leftSegmentIndex]);
            List<int> rightPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[rightSegmentIndex]);
            if (leftPrimaryAtomIndices.Count == 0 || rightPrimaryAtomIndices.Count == 0)
                return false;

            int bestBoundaryAtomIndex = -1;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int maxLeftSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, leftPrimaryAtomIndices.Count - 1));
            int maxRightSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, rightPrimaryAtomIndices.Count - 1));
            for (int leftSkip = 0; leftSkip <= maxLeftSkip; leftSkip++)
            {
                for (int rightSkip = 0; rightSkip <= maxRightSkip; rightSkip++)
                {
                    int leftPosition = leftPrimaryAtomIndices.Count - 1 - leftSkip;
                    int rightPosition = rightSkip;
                    if (leftPosition < 0 || rightPosition >= rightPrimaryAtomIndices.Count)
                        continue;

                    int candidateMatchedAtomCount = 0;
                    int candidateMatchedUniqueLaneCount = 0;
                    int strongContextMatchCount = 0;
                    int oppositeDirectionMatchCount = 0;
                    HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
                    int currentLeftPosition = leftPosition;
                    int currentRightPosition = rightPosition;
                    while (currentLeftPosition >= 0 && currentRightPosition < rightPrimaryAtomIndices.Count)
                    {
                        TrackAtom previousAtom = chain.TrackAtoms[leftPrimaryAtomIndices[currentLeftPosition]];
                        TrackAtom currentAtom = chain.TrackAtoms[rightPrimaryAtomIndices[currentRightPosition]];
                        if (!TryMatchReverseRepeatedPrimaryAtom(
                                previousAtom,
                                currentAtom,
                                out bool strongContextMatch,
                                out bool oppositeDirectionMatch))
                        {
                            break;
                        }

                        candidateMatchedAtomCount++;
                        if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                            candidateMatchedUniqueLaneCount++;
                        if (strongContextMatch)
                            strongContextMatchCount++;
                        if (oppositeDirectionMatch)
                            oppositeDirectionMatchCount++;

                        currentLeftPosition--;
                        currentRightPosition++;
                    }

                    bool qualifies = candidateMatchedAtomCount >= TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                        && candidateMatchedUniqueLaneCount >= TURNBACK_REPEAT_MIN_UNIQUE_LANES
                        && (strongContextMatchCount > 0 || oppositeDirectionMatchCount >= 2);
                    if (!qualifies)
                        continue;

                    bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                        || (candidateMatchedAtomCount == bestMatchedAtomCount
                            && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                    if (!better)
                        continue;

                    bestMatchedAtomCount = candidateMatchedAtomCount;
                    bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                    bestBoundaryAtomIndex = rightPrimaryAtomIndices[rightPosition];
                }
            }

            if (bestBoundaryAtomIndex < 0)
                return false;

            boundaryAtomIndex = bestBoundaryAtomIndex;
            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            return true;
        }

        private bool TryFindAdjacentSegmentTurnbackBoundary(
            LineTrackChain chain,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount,
            out int segmentPairIndex,
            out string note)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            segmentPairIndex = -1;
            note = "no-adjacent-reverse-overlap";
            if (chain == null || chain.SegmentRanges.Count < 2)
            {
                note = "insufficient-segments";
                return false;
            }

            bool foundAnyPrimary = false;
            int bestBoundaryAtomIndex = -1;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int bestSegmentPairIndex = -1;
            for (int segmentIndex = 1; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                List<int> leftPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[segmentIndex - 1]);
                List<int> rightPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[segmentIndex]);
                if (leftPrimaryAtomIndices.Count > 0 && rightPrimaryAtomIndices.Count > 0)
                    foundAnyPrimary = true;

                if (!TryMeasureAdjacentSegmentReverseOverlap(
                        chain,
                        segmentIndex - 1,
                        segmentIndex,
                        out int candidateBoundaryAtomIndex,
                        out int candidateMatchedAtomCount,
                        out int candidateMatchedUniqueLaneCount))
                {
                    continue;
                }

                bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                    || (candidateMatchedAtomCount == bestMatchedAtomCount
                        && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                if (!better)
                    continue;

                bestBoundaryAtomIndex = candidateBoundaryAtomIndex;
                bestMatchedAtomCount = candidateMatchedAtomCount;
                bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                bestSegmentPairIndex = segmentIndex - 1;
            }

            if (bestBoundaryAtomIndex < 0)
            {
                if (!foundAnyPrimary)
                    note = "adjacent-segments-without-primary";
                return false;
            }

            boundaryAtomIndex = bestBoundaryAtomIndex;
            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            segmentPairIndex = bestSegmentPairIndex;
            note = "segment-pair=" + bestSegmentPairIndex + " match=" + bestMatchedAtomCount + " unique=" + bestMatchedUniqueLaneCount;
            return true;
        }

        private static List<int> CollectPrimaryAtomIndicesForWindow(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return primaryAtomIndices;
            }

            int start = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count);
            int end = math.clamp(endAtomIndexExclusive, start, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private static int NormalizeCircularBoundary(int atomIndex, int atomCount)
        {
            if (atomCount <= 0)
                return 0;

            int normalized = atomIndex % atomCount;
            if (normalized < 0)
                normalized += atomCount;
            return normalized;
        }

        private static void AppendPrimaryAtomIndicesForRange(
            LineTrackChain chain,
            List<int> primaryAtomIndices,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null
                || primaryAtomIndices == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return;
            }

            int start = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count);
            int end = math.clamp(endAtomIndexExclusive, start, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }
        }

        private static List<int> CollectPrimaryAtomIndicesForCircularWindow(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return primaryAtomIndices;

            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return primaryAtomIndices;

            if (start < end)
            {
                AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, start, end);
                return primaryAtomIndices;
            }

            AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, start, atomCount);
            AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, 0, end);
            return primaryAtomIndices;
        }

        private static Entity[] ReversePhysicalLaneKeys(Entity[] keys)
        {
            if (keys == null || keys.Length == 0)
                return Array.Empty<Entity>();

            Entity[] reversed = new Entity[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                reversed[i] = keys[keys.Length - 1 - i];
            return reversed;
        }

        private static int ComputeOrderedPhysicalKeyLcsLength(Entity[] left, Entity[] right)
        {
            if (left == null || right == null || left.Length == 0 || right.Length == 0)
                return 0;

            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int leftIndex = 0; leftIndex < left.Length; leftIndex++)
            {
                Entity leftKey = left[leftIndex];
                for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                {
                    if (leftKey == right[rightIndex - 1])
                        current[rightIndex] = previous[rightIndex - 1] + 1;
                    else
                        current[rightIndex] = math.max(previous[rightIndex], current[rightIndex - 1]);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
                Array.Clear(current, 0, current.Length);
            }

            return previous[right.Length];
        }

        private bool TryMeasureReverseOverlapBetweenLaneSequences(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int matchedLaneCount)
        {
            matchedLaneCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            matchedLaneCount = ComputeOrderedPhysicalKeyLcsLength(
                inboundKeys,
                ReversePhysicalLaneKeys(outboundKeys));
            if (matchedLaneCount < 2)
                return false;

            float coverageRatio = matchedLaneCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.35f;
        }

        private bool TryMeasureReverseOverlapBetweenTrackContainerSequences(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int matchedContainerCount,
            out int matchedUniqueContainerCount)
        {
            matchedContainerCount = 0;
            matchedUniqueContainerCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            matchedContainerCount = ComputeOrderedPhysicalKeyLcsLength(
                inboundKeys,
                ReversePhysicalLaneKeys(outboundKeys));
            if (matchedContainerCount < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS)
                return false;

            HashSet<Entity> inboundDistinct = new HashSet<Entity>(inboundKeys);
            HashSet<Entity> matchedDistinct = new HashSet<Entity>();
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundDistinct.Contains(outboundKeys[i]))
                    matchedDistinct.Add(outboundKeys[i]);
            }

            matchedUniqueContainerCount = matchedDistinct.Count;
            if (matchedUniqueContainerCount <= 0)
                return false;

            float coverageRatio = matchedContainerCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.35f;
        }

        private bool TryMeasureSharedPhysicalLaneSetOverlap(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int sharedLaneCount)
        {
            sharedLaneCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            HashSet<Entity> inboundSet = new HashSet<Entity>(inboundKeys);
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundSet.Contains(outboundKeys[i]))
                    sharedLaneCount++;
            }

            if (sharedLaneCount < 4)
                return false;

            float coverageRatio = sharedLaneCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.20f;
        }

        private bool TryMeasureSharedTrackContainerSetOverlap(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int sharedContainerCount)
        {
            sharedContainerCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            HashSet<Entity> inboundSet = new HashSet<Entity>(inboundKeys);
            HashSet<Entity> sharedSet = new HashSet<Entity>();
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundSet.Contains(outboundKeys[i]))
                    sharedSet.Add(outboundKeys[i]);
            }

            sharedContainerCount = sharedSet.Count;
            if (sharedContainerCount <= 0)
                return false;

            float coverageRatio = sharedContainerCount / (float)math.max(1, math.min(inboundSet.Count, new HashSet<Entity>(outboundKeys).Count));
            return coverageRatio >= 0.20f;
        }

        private bool TryMeasureReverseOverlapBetweenPrimaryWindows(
            LineTrackChain chain,
            List<int> inboundPrimaryAtomIndices,
            List<int> outboundPrimaryAtomIndices,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || inboundPrimaryAtomIndices == null
                || outboundPrimaryAtomIndices == null
                || inboundPrimaryAtomIndices.Count == 0
                || outboundPrimaryAtomIndices.Count == 0)
            {
                return false;
            }

            const float minCoverageRatio = 0.35f;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int maxLeftSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, inboundPrimaryAtomIndices.Count - 1));
            int maxRightSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, outboundPrimaryAtomIndices.Count - 1));
            for (int leftSkip = 0; leftSkip <= maxLeftSkip; leftSkip++)
            {
                for (int rightSkip = 0; rightSkip <= maxRightSkip; rightSkip++)
                {
                    int leftPosition = inboundPrimaryAtomIndices.Count - 1 - leftSkip;
                    int rightPosition = rightSkip;
                    if (leftPosition < 0 || rightPosition >= outboundPrimaryAtomIndices.Count)
                        continue;

                    int candidateMatchedAtomCount = 0;
                    int candidateMatchedUniqueLaneCount = 0;
                    int strongContextMatchCount = 0;
                    int oppositeDirectionMatchCount = 0;
                    HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
                    int currentLeftPosition = leftPosition;
                    int currentRightPosition = rightPosition;
                    while (currentLeftPosition >= 0 && currentRightPosition < outboundPrimaryAtomIndices.Count)
                    {
                        TrackAtom previousAtom = chain.TrackAtoms[inboundPrimaryAtomIndices[currentLeftPosition]];
                        TrackAtom currentAtom = chain.TrackAtoms[outboundPrimaryAtomIndices[currentRightPosition]];
                        if (!TryMatchReverseRepeatedPrimaryAtom(
                                previousAtom,
                                currentAtom,
                                out bool strongContextMatch,
                                out bool oppositeDirectionMatch))
                        {
                            break;
                        }

                        candidateMatchedAtomCount++;
                        if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                            candidateMatchedUniqueLaneCount++;
                        if (strongContextMatch)
                            strongContextMatchCount++;
                        if (oppositeDirectionMatch)
                            oppositeDirectionMatchCount++;

                        currentLeftPosition--;
                        currentRightPosition++;
                    }

                    if (candidateMatchedAtomCount < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                        || candidateMatchedUniqueLaneCount < TURNBACK_REPEAT_MIN_UNIQUE_LANES
                        || (strongContextMatchCount <= 0 && oppositeDirectionMatchCount < 2))
                    {
                        continue;
                    }

                    float coverageRatio = candidateMatchedAtomCount / (float)math.max(1, math.min(inboundPrimaryAtomIndices.Count, outboundPrimaryAtomIndices.Count));
                    if (coverageRatio < minCoverageRatio)
                        continue;

                    bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                        || (candidateMatchedAtomCount == bestMatchedAtomCount
                            && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                    if (!better)
                        continue;

                    bestMatchedAtomCount = candidateMatchedAtomCount;
                    bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                }
            }

            if (bestMatchedAtomCount <= 0)
                return false;

            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            return true;
        }

        private bool TryConfirmTurnbackBoundaryAtStationPass(
            LineTrackChain chain,
            List<StationPassRange> stationPasses,
            int stationPassIndex,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount,
            out string note)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            note = string.Empty;
            if (chain == null
                || stationPasses == null
                || stationPassIndex < 0
                || stationPassIndex >= stationPasses.Count)
            {
                return false;
            }

            StationPassRange currentPass = stationPasses[stationPassIndex];
            if (currentPass.Building == Entity.Null || currentPass.WaypointIndex < 0)
                return false;

            int stationPassCount = stationPasses.Count;
            if (stationPassCount < 2)
                return false;

            int previousPassIndex = (stationPassIndex - 1 + stationPassCount) % stationPassCount;
            int nextPassIndex = (stationPassIndex + 1) % stationPassCount;
            StationPassRange previousPass = stationPasses[previousPassIndex];
            StationPassRange nextPass = stationPasses[nextPassIndex];

            bool isMirrorCandidate = stationPassCount >= 3
                && previousPass.Building != Entity.Null
                && previousPass.Building == nextPass.Building;
            bool isLapSeamCandidate = stationPassIndex == stationPassCount - 1;
            if (!isMirrorCandidate && !isLapSeamCandidate)
                return false;

            int inboundStartAtomIndex = previousPass.EndAtomIndexExclusive;
            int inboundEndAtomIndexExclusive = currentPass.StartAtomIndex;
            int outboundStartAtomIndex = currentPass.EndAtomIndexExclusive;
            int outboundEndAtomIndexExclusive = nextPass.StartAtomIndex;

            List<int> inboundPrimaryAtomIndices = CollectPrimaryAtomIndicesForCircularWindow(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            List<int> outboundPrimaryAtomIndices = CollectPrimaryAtomIndicesForCircularWindow(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundPrimaryAtomIndices.Count == 0
                || outboundPrimaryAtomIndices.Count == 0)
            {
                return false;
            }

            if (!TryMeasureReverseOverlapBetweenPrimaryWindows(
                    chain,
                    inboundPrimaryAtomIndices,
                    outboundPrimaryAtomIndices,
                    out matchedAtomCount,
                    out matchedUniqueLaneCount))
            {
                if (!TryMeasureReverseOverlapBetweenLaneSequences(
                        chain,
                        inboundStartAtomIndex,
                        inboundEndAtomIndexExclusive,
                        outboundStartAtomIndex,
                        outboundEndAtomIndexExclusive,
                        out int matchedLaneCount))
                {
                    if (!TryMeasureSharedPhysicalLaneSetOverlap(
                            chain,
                            inboundStartAtomIndex,
                            inboundEndAtomIndexExclusive,
                            outboundStartAtomIndex,
                            outboundEndAtomIndexExclusive,
                            out int sharedLaneCount))
                    {
                        if (!TryMeasureReverseOverlapBetweenTrackContainerSequences(
                                chain,
                                inboundStartAtomIndex,
                                inboundEndAtomIndexExclusive,
                                outboundStartAtomIndex,
                                outboundEndAtomIndexExclusive,
                                out int matchedContainerCount,
                                out int matchedUniqueContainerCount))
                        {
                            if (!TryMeasureSharedTrackContainerSetOverlap(
                                    chain,
                                    inboundStartAtomIndex,
                                    inboundEndAtomIndexExclusive,
                                    outboundStartAtomIndex,
                                    outboundEndAtomIndexExclusive,
                                    out int sharedContainerCount))
                            {
                                return false;
                            }

                            matchedAtomCount = sharedContainerCount;
                            matchedUniqueLaneCount = sharedContainerCount;
                            boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                            note = (isMirrorCandidate ? "mirror-stop-container-set" : "seam-stop-container-set")
                                + " wp=" + currentPass.WaypointIndex
                                + " match=" + sharedContainerCount;
                            return true;
                        }

                        matchedAtomCount = matchedContainerCount;
                        matchedUniqueLaneCount = matchedUniqueContainerCount;
                        boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                        note = (isMirrorCandidate ? "mirror-stop-container" : "seam-stop-container")
                            + " wp=" + currentPass.WaypointIndex
                            + " match=" + matchedContainerCount
                            + " unique=" + matchedUniqueContainerCount;
                        return true;
                    }

                    matchedAtomCount = sharedLaneCount;
                    matchedUniqueLaneCount = sharedLaneCount;
                    boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                    note = (isMirrorCandidate ? "mirror-stop-set" : "seam-stop-set")
                        + " wp=" + currentPass.WaypointIndex
                        + " match=" + sharedLaneCount;
                    return true;
                }

                matchedAtomCount = matchedLaneCount;
                matchedUniqueLaneCount = matchedLaneCount;
                boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                note = (isMirrorCandidate ? "mirror-stop-lcs" : "seam-stop-lcs")
                    + " wp=" + currentPass.WaypointIndex
                    + " match=" + matchedLaneCount;
                return true;
            }

            boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
            note = (isMirrorCandidate ? "mirror-stop" : "seam-stop")
                + " wp=" + currentPass.WaypointIndex
                + " match=" + matchedAtomCount
                + " unique=" + matchedUniqueLaneCount;
            return true;
        }

        private int ResolveTraversalRunSliceIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            if (chain?.TraversalProfile == null
                || chain.TraversalProfile.RunSlices == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return -1;
            }

            int clampedAtomIndex = math.clamp(atomIndex, 0, chain.TrackAtoms.Count - 1);
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                if (clampedAtomIndex >= slice.StartAtomIndex
                    && clampedAtomIndex < slice.EndAtomIndexExclusive)
                {
                    return slice.SliceIndex;
                }
            }

            return -1;
        }

        private int ResolveTurnbackBoundaryEventIndex(LineTrackChain chain, int boundaryAtomIndex)
        {
            if (chain?.TraversalProfile == null || chain.TraversalProfile.Events == null)
                return -1;

            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.StartAtomIndex == boundaryAtomIndex
                    || traversalEvent.EndAtomIndexExclusive == boundaryAtomIndex)
                {
                    return traversalEvent.EventIndex;
                }
            }

            return -1;
        }

        private int ResolveVehicleTargetWaypointIndex(Entity vehicle)
        {
            if (vehicle == Entity.Null || !EntityManager.HasComponent<Target>(vehicle))
                return -1;

            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || !EntityManager.HasComponent<Waypoint>(target))
                return -1;

            return EntityManager.GetComponentData<Waypoint>(target).m_Index;
        }

        internal void BuildRunChartTurnbacks(LineTrackChain chain, Entity line)
        {
            if (chain == null)
                return;

            chain.RunChartTurnbackRegions.Clear();
            if (TransportModeResolver.Resolve(EntityManager, line) != TransitMode.Tram)
                return;

            var spans = new List<TramEdgeSpan>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                if (TryReadTramEdgeSpan(chain.TrackAtoms[atomIndex], atomIndex, out TramEdgeSpan span))
                    spans.Add(span);
            }
            if (spans.Count < 2)
                return;

            TramTurnbackBuild published = null;
            for (int laterIndex = 1; laterIndex < spans.Count; laterIndex++)
            {
                TramEdgeSpan later = spans[laterIndex];
                int earlierIndex = FindReverseSpan(spans, laterIndex, later);
                if (earlierIndex < 0)
                    continue;

                var region = new TramTurnbackBuild(laterIndex, earlierIndex);
                AddOverlap(region, spans[earlierIndex], later);
                int forward = laterIndex + 1;
                int reverse = earlierIndex - 1;
                while (forward < spans.Count && reverse >= 0
                    && TryContinuousTramOverlap(
                        region.Overlaps[region.Overlaps.Count - 1],
                        spans[reverse + 1],
                        spans[forward - 1],
                        spans[reverse],
                        spans[forward],
                        out float low,
                        out float high,
                        out Entity sharedNode))
                {
                    region.SetLastEndNode(sharedNode);
                    AddOverlap(region, spans[forward], low, high, sharedNode, Entity.Null);
                    region.LastSpanIndex = forward;
                    region.SetEarliestEarlierSpanIndex(reverse);
                    forward++;
                    reverse--;
                }

                if (!QualifiesRunChartTurnback(region))
                    continue;

                if (published == null)
                {
                    published = region;
                    continue;
                }

                if (IsRunChartTurnbackContinuation(published, region))
                {
                    published.Extend(region);
                    continue;
                }

                PublishRunChartTurnback(chain, spans, published);
                published = region;
            }
            if (published != null)
                PublishRunChartTurnback(chain, spans, published);
            chain.RunChartTurnbackRegions.Sort((left, right) =>
                left.BoundaryAtomIndex.CompareTo(right.BoundaryAtomIndex));
        }

        private static bool IsRunChartTurnbackContinuation(
            TramTurnbackBuild current,
            TramTurnbackBuild next)
        {
            if (next.FirstSpanIndex <= current.LastSpanIndex)
                return true;

            return next.FirstSpanIndex == current.LastSpanIndex + 1
                && next.FirstEarlierSpanIndex < current.FirstEarlierSpanIndex;
        }

        private static void PublishRunChartTurnback(
            LineTrackChain chain,
            List<TramEdgeSpan> spans,
            TramTurnbackBuild region)
        {
            chain.RunChartTurnbackRegions.Add(new RunChartTurnbackRegion(
                spans[region.FirstSpanIndex].AtomIndex,
                spans[region.FirstSpanIndex].AtomIndex,
                spans[region.LastSpanIndex].AtomIndex + 1));
        }

        internal void AppendRunChartTramDump(StringBuilder sb, LineTrackChain chain, Entity line)
        {
            if (sb == null
                || chain == null
                || TransportModeResolver.Resolve(EntityManager, line) != TransitMode.Tram)
            {
                return;
            }

            sb.Append("tramTraversalEvents:");
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent item = chain.TraversalProfile.Events[eventIndex];
                if (item.Kind != TraversalEventKind.Stop
                    && item.Kind != TraversalEventKind.Pass
                    && item.Kind != TraversalEventKind.BreakBoundary)
                {
                    continue;
                }

                string name = string.Empty;
                if (item.Building != Entity.Null)
                {
                    m_Support.TryGetRenderedLabelName(item.Building, out name);
                    if (string.IsNullOrWhiteSpace(name))
                        name = m_Support.StopName(item.Building);
                }
                sb.Append(" | e").Append(item.EventIndex)
                  .Append(" kind=").Append(item.Kind)
                  .Append(" station=").Append(item.StationId ?? string.Empty)
                  .Append(" name=").Append(string.IsNullOrWhiteSpace(name) ? "-" : name)
                  .Append(" building=").Append(item.Building.Index)
                  .Append(" wp=").Append(item.WaypointIndex)
                  .Append(" pass=").Append(item.PassIndex)
                  .Append(" atoms=").Append(item.StartAtomIndex)
                  .Append("..").Append(item.EndAtomIndexExclusive);
            }
            sb.AppendLine();

            sb.Append("tramSameStationAdjacent:");
            TraversalEvent? previousStation = null;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent item = chain.TraversalProfile.Events[eventIndex];
                if (item.Kind != TraversalEventKind.Stop && item.Kind != TraversalEventKind.Pass)
                    continue;
                if (previousStation.HasValue
                    && !string.IsNullOrEmpty(item.StationId)
                    && string.Equals(previousStation.Value.StationId, item.StationId, StringComparison.Ordinal))
                {
                    TraversalEvent previous = previousStation.Value;
                    sb.Append(" | station=").Append(item.StationId)
                      .Append(" from=e").Append(previous.EventIndex)
                      .Append('/').Append(previous.Kind)
                      .Append("/wp").Append(previous.WaypointIndex)
                      .Append("/a").Append(previous.StartAtomIndex)
                      .Append(" to=e").Append(item.EventIndex)
                      .Append('/').Append(item.Kind)
                      .Append("/wp").Append(item.WaypointIndex)
                      .Append("/a").Append(item.StartAtomIndex);
                }
                previousStation = item;
            }
            sb.AppendLine();

            var spans = new List<TramEdgeSpan>();
            int unreadableAtoms = 0;
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                if (TryReadTramEdgeSpan(chain.TrackAtoms[atomIndex], atomIndex, out TramEdgeSpan span))
                    spans.Add(span);
                else
                    unreadableAtoms++;
            }

            sb.Append("tramEdgeSpans: count=").Append(spans.Count)
              .Append(" unreadableAtoms=").Append(unreadableAtoms);
            for (int spanIndex = 0; spanIndex < spans.Count; spanIndex++)
            {
                TramEdgeSpan span = spans[spanIndex];
                TrackAtom atom = chain.TrackAtoms[span.AtomIndex];
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(atom.Key.PhysicalLaneKey);
                sb.Append(" | s").Append(spanIndex)
                  .Append(" atom=").Append(span.AtomIndex)
                  .Append(" lane=").Append(atom.Key.PhysicalLaneKey.Index)
                  .Append(" edge=").Append(span.Edge.Index)
                  .Append(" dir=").Append(span.Forward ? "+" : "-")
                  .Append(" target=").Append(atom.TargetDelta.x.ToString("0.0000"))
                  .Append("..").Append(atom.TargetDelta.y.ToString("0.0000"))
                  .Append(" edgeLane=").Append(edgeLane.m_EdgeDelta.x.ToString("0.0000"))
                  .Append("..").Append(edgeLane.m_EdgeDelta.y.ToString("0.0000"))
                  .Append(" mapped=").Append(span.Low.ToString("0.0000"))
                  .Append("..").Append(span.High.ToString("0.0000"))
                  .Append(" meters=").Append(MeasureEdgeSpan(span).ToString("0.0"));
            }
            sb.AppendLine();

            sb.Append("tramReverseCandidates:");
            int candidateCount = 0;
            for (int laterIndex = 1; laterIndex < spans.Count; laterIndex++)
            {
                TramEdgeSpan later = spans[laterIndex];
                int earlierIndex = FindReverseSpan(spans, laterIndex, later);
                if (earlierIndex < 0)
                    continue;

                candidateCount++;
                var region = new TramTurnbackBuild(laterIndex, earlierIndex);
                AddOverlap(region, spans[earlierIndex], later);
                int forward = laterIndex + 1;
                int reverse = earlierIndex - 1;
                while (forward < spans.Count && reverse >= 0
                    && TryContinuousTramOverlap(
                        region.Overlaps[region.Overlaps.Count - 1],
                        spans[reverse + 1],
                        spans[forward - 1],
                        spans[reverse],
                        spans[forward],
                        out float low,
                        out float high,
                        out Entity sharedNode))
                {
                    region.SetLastEndNode(sharedNode);
                    AddOverlap(region, spans[forward], low, high, sharedNode, Entity.Null);
                    region.LastSpanIndex = forward;
                    forward++;
                    reverse--;
                }

                MeasureRunChartTurnback(region, out float meters, out int edgeCount, out bool singleEdgeLongEnough);
                bool qualified = meters >= 400f && (edgeCount >= 2 || singleEdgeLongEnough);
                string stopReason = forward >= spans.Count
                    ? "later-end"
                    : reverse < 0
                        ? "earlier-start"
                        : DescribeTramContinuityFailure(
                            region.Overlaps[region.Overlaps.Count - 1],
                            spans[reverse + 1],
                            spans[forward - 1],
                            spans[reverse],
                            spans[forward]);
                sb.Append(" | c").Append(candidateCount - 1)
                  .Append(" earlier=s").Append(earlierIndex)
                  .Append("/a").Append(spans[earlierIndex].AtomIndex)
                  .Append(" later=s").Append(laterIndex)
                  .Append("/a").Append(later.AtomIndex)
                  .Append(" last=s").Append(region.LastSpanIndex)
                  .Append(" overlaps=").Append(region.Overlaps.Count)
                  .Append(" edges=").Append(edgeCount)
                  .Append(" meters=").Append(meters.ToString("0.0"))
                  .Append(" result=").Append(qualified ? "accepted" : "below-400m")
                  .Append(" stop=").Append(stopReason);
            }
            if (candidateCount == 0)
                sb.Append(" none");
            sb.AppendLine();

            sb.Append("tramRunChartTurnbackRegions:");
            for (int regionIndex = 0; regionIndex < chain.RunChartTurnbackRegions.Count; regionIndex++)
            {
                RunChartTurnbackRegion region = chain.RunChartTurnbackRegions[regionIndex];
                sb.Append(" | r").Append(regionIndex)
                  .Append(" boundary=").Append(region.BoundaryAtomIndex)
                  .Append(" atoms=").Append(region.StartAtomIndex)
                  .Append("..").Append(region.EndAtomIndexExclusive);
            }
            if (chain.RunChartTurnbackRegions.Count == 0)
                sb.Append(" none");
            sb.AppendLine();
        }

        private bool TryReadTramEdgeSpan(TrackAtom atom, int atomIndex, out TramEdgeSpan span)
        {
            span = default;
            Entity lane = atom.Key.PhysicalLaneKey;
            if (lane == Entity.Null
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<EdgeLane>(lane)
                || !EntityManager.HasComponent<Owner>(lane))
            {
                return false;
            }

            Entity edge = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (edge == Entity.Null
                || !EntityManager.Exists(edge)
                || !EntityManager.HasComponent<Game.Net.Edge>(edge)
                || !EntityManager.HasComponent<Curve>(edge))
            {
                return false;
            }

            EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(lane);
            float start = math.lerp(edgeLane.m_EdgeDelta.x, edgeLane.m_EdgeDelta.y, atom.TargetDelta.x);
            float end = math.lerp(edgeLane.m_EdgeDelta.x, edgeLane.m_EdgeDelta.y, atom.TargetDelta.y);
            if (math.abs(end - start) <= 0.0001f)
                return false;

            span = new TramEdgeSpan
            {
                AtomIndex = atomIndex,
                Edge = edge,
                Forward = end > start,
                Low = math.min(start, end),
                High = math.max(start, end)
            };
            return true;
        }

        private static int FindReverseSpan(List<TramEdgeSpan> spans, int laterIndex, TramEdgeSpan later)
        {
            for (int earlierIndex = laterIndex - 1; earlierIndex >= 0; earlierIndex--)
            {
                if (TryReverseOverlap(spans[earlierIndex], later, out _, out _))
                    return earlierIndex;
            }
            return -1;
        }

        private static bool TryReverseOverlap(TramEdgeSpan earlier, TramEdgeSpan later, out float low, out float high)
        {
            low = math.max(earlier.Low, later.Low);
            high = math.min(earlier.High, later.High);
            return earlier.Edge == later.Edge
                && earlier.Forward != later.Forward
                && high - low > 0.0001f;
        }

        private bool TryContinuousTramOverlap(
            TramOverlap previous,
            TramEdgeSpan previousEarlier,
            TramEdgeSpan previousLater,
            TramEdgeSpan nextEarlier,
            TramEdgeSpan nextLater,
            out float low,
            out float high,
            out Entity sharedNode)
        {
            low = 0f;
            high = 0f;
            sharedNode = Entity.Null;
            if (!TryReverseOverlap(nextEarlier, nextLater, out low, out high))
            {
                return false;
            }
            if (previous.Edge == nextLater.Edge)
            {
                return math.abs(EndParam(previousLater, previous.Low, previous.High)
                        - StartParam(nextLater, low, high)) <= 0.0001f
                    && math.abs(StartParam(previousEarlier, previous.Low, previous.High)
                        - EndParam(nextEarlier, low, high)) <= 0.0001f;
            }
            if (!TrySharedNode(previous.Edge, nextLater.Edge, out Entity[] nodes))
            {
                return false;
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                Entity node = nodes[i];
                if (EndsAt(previousLater, previous.Low, previous.High, node)
                    && StartsAt(nextLater, low, high, node)
                    && StartsAt(previousEarlier, previous.Low, previous.High, node)
                    && EndsAt(nextEarlier, low, high, node))
                {
                    sharedNode = node;
                    return true;
                }
            }
            return false;
        }

        private bool TrySharedNode(Entity left, Entity right, out Entity[] nodes)
        {
            nodes = Array.Empty<Entity>();
            if (left == Entity.Null || right == Entity.Null
                || !EntityManager.HasComponent<Game.Net.Edge>(left)
                || !EntityManager.HasComponent<Game.Net.Edge>(right))
            {
                return false;
            }

            Game.Net.Edge leftEdge = EntityManager.GetComponentData<Game.Net.Edge>(left);
            Game.Net.Edge rightEdge = EntityManager.GetComponentData<Game.Net.Edge>(right);
            var shared = new List<Entity>(2);
            AddSharedNode(shared, leftEdge.m_Start, rightEdge.m_Start);
            AddSharedNode(shared, leftEdge.m_Start, rightEdge.m_End);
            AddSharedNode(shared, leftEdge.m_End, rightEdge.m_Start);
            AddSharedNode(shared, leftEdge.m_End, rightEdge.m_End);
            nodes = shared.ToArray();
            return nodes.Length > 0;
        }

        private static void AddSharedNode(List<Entity> nodes, Entity left, Entity right)
        {
            if (left != Entity.Null && left == right && !nodes.Contains(left))
                nodes.Add(left);
        }

        private bool StartsAt(TramEdgeSpan span, float low, float high, Entity node)
        {
            return EdgeParam(span.Edge, node, out float value)
                && math.abs(StartParam(span, low, high) - value) <= 0.0001f;
        }

        private bool EndsAt(TramEdgeSpan span, float low, float high, Entity node)
        {
            return EdgeParam(span.Edge, node, out float value)
                && math.abs(EndParam(span, low, high) - value) <= 0.0001f;
        }

        private static float StartParam(TramEdgeSpan span, float low, float high) =>
            span.Forward ? low : high;

        private static float EndParam(TramEdgeSpan span, float low, float high) =>
            span.Forward ? high : low;

        private bool EdgeParam(Entity edge, Entity node, out float value)
        {
            value = 0f;
            if (edge == Entity.Null || node == Entity.Null
                || !EntityManager.HasComponent<Game.Net.Edge>(edge))
            {
                return false;
            }

            Game.Net.Edge edgeData = EntityManager.GetComponentData<Game.Net.Edge>(edge);
            if (edgeData.m_Start == node)
                return true;
            if (edgeData.m_End == node)
            {
                value = 1f;
                return true;
            }
            return false;
        }

        private static void AddOverlap(TramTurnbackBuild region, TramEdgeSpan span, TramEdgeSpan other)
        {
            AddOverlap(region, span, math.max(span.Low, other.Low), math.min(span.High, other.High));
        }

        private static void AddOverlap(
            TramTurnbackBuild region,
            TramEdgeSpan span,
            float low,
            float high,
            Entity startNode = default,
            Entity endNode = default)
        {
            if (high - low <= 0.0001f)
                return;
            if (!region.Intervals.TryGetValue(span.Edge, out List<EdgeInterval> intervals))
            {
                intervals = new List<EdgeInterval>();
                region.Intervals[span.Edge] = intervals;
            }
            intervals.Add(new EdgeInterval(low, high));
            region.Overlaps.Add(new TramOverlap(span.Edge, low, high, startNode, endNode));
        }

        private bool QualifiesRunChartTurnback(TramTurnbackBuild region)
        {
            MeasureRunChartTurnback(region, out float totalLength, out int edgeCount, out bool singleEdgeLongEnough);
            return totalLength >= 400f && (edgeCount >= 2 || singleEdgeLongEnough);
        }

        private void MeasureRunChartTurnback(
            TramTurnbackBuild region,
            out float totalLength,
            out int edgeCount,
            out bool singleEdgeLongEnough)
        {
            totalLength = 0f;
            edgeCount = 0;
            singleEdgeLongEnough = false;
            if (region == null)
                return;
            foreach (KeyValuePair<Entity, List<EdgeInterval>> entry in region.Intervals)
            {
                float length = UnionEdgeLength(entry.Key, entry.Value);
                if (length <= 0f)
                    continue;
                edgeCount++;
                totalLength += length;
                singleEdgeLongEnough |= length >= 400f;
            }
        }

        private float MeasureEdgeSpan(TramEdgeSpan span)
        {
            if (span.Edge == Entity.Null || !EntityManager.HasComponent<Curve>(span.Edge))
                return 0f;
            Curve curve = EntityManager.GetComponentData<Curve>(span.Edge);
            return MathUtils.Length(MathUtils.Cut(curve.m_Bezier, new float2(span.Low, span.High)));
        }

        private string DescribeTramContinuityFailure(
            TramOverlap previous,
            TramEdgeSpan previousEarlier,
            TramEdgeSpan previousLater,
            TramEdgeSpan nextEarlier,
            TramEdgeSpan nextLater)
        {
            if (!TryReverseOverlap(nextEarlier, nextLater, out float low, out float high))
                return "next-no-reverse-overlap";
            if (previous.Edge == nextLater.Edge)
            {
                float laterGap = math.abs(EndParam(previousLater, previous.Low, previous.High)
                    - StartParam(nextLater, low, high));
                float earlierGap = math.abs(StartParam(previousEarlier, previous.Low, previous.High)
                    - EndParam(nextEarlier, low, high));
                return "same-edge-gap-later=" + laterGap.ToString("0.0000")
                    + "-earlier=" + earlierGap.ToString("0.0000");
            }
            if (!TrySharedNode(previous.Edge, nextLater.Edge, out Entity[] nodes))
                return "edges-not-connected";
            for (int i = 0; i < nodes.Length; i++)
            {
                Entity node = nodes[i];
                if (EndsAt(previousLater, previous.Low, previous.High, node)
                    && StartsAt(nextLater, low, high, node)
                    && StartsAt(previousEarlier, previous.Low, previous.High, node)
                    && EndsAt(nextEarlier, low, high, node))
                {
                    return "continuous";
                }
            }
            return "shared-node-not-at-overlap-end";
        }

        private float UnionEdgeLength(Entity edge, List<EdgeInterval> intervals)
        {
            if (edge == Entity.Null || intervals == null || intervals.Count == 0
                || !EntityManager.HasComponent<Curve>(edge))
            {
                return 0f;
            }

            intervals.Sort((left, right) => left.Low.CompareTo(right.Low));
            Curve curve = EntityManager.GetComponentData<Curve>(edge);
            float length = 0f;
            float low = intervals[0].Low;
            float high = intervals[0].High;
            for (int i = 1; i <= intervals.Count; i++)
            {
                if (i < intervals.Count && intervals[i].Low <= high + 0.0001f)
                {
                    high = math.max(high, intervals[i].High);
                    continue;
                }

                length += MathUtils.Length(MathUtils.Cut(curve.m_Bezier, new float2(low, high)));
                if (i < intervals.Count)
                {
                    low = intervals[i].Low;
                    high = intervals[i].High;
                }
            }
            return length;
        }

        private struct TramEdgeSpan
        {
            internal int AtomIndex;
            internal Entity Edge;
            internal bool Forward;
            internal float Low;
            internal float High;
        }

        private readonly struct EdgeInterval
        {
            internal readonly float Low;
            internal readonly float High;

            internal EdgeInterval(float low, float high)
            {
                Low = low;
                High = high;
            }
        }

        private readonly struct TramOverlap
        {
            internal readonly Entity Edge;
            internal readonly float Low;
            internal readonly float High;
            internal readonly Entity StartNode;
            internal readonly Entity EndNode;

            internal TramOverlap(Entity edge, float low, float high, Entity startNode, Entity endNode)
            {
                Edge = edge;
                Low = low;
                High = high;
                StartNode = startNode;
                EndNode = endNode;
            }

            internal TramOverlap WithEndNode(Entity node) =>
                new TramOverlap(Edge, Low, High, StartNode, node);
        }

        private sealed class TramTurnbackBuild
        {
            internal readonly Dictionary<Entity, List<EdgeInterval>> Intervals =
                new Dictionary<Entity, List<EdgeInterval>>();
            internal readonly List<TramOverlap> Overlaps = new List<TramOverlap>();
            internal readonly int FirstSpanIndex;
            internal int FirstEarlierSpanIndex;
            internal int LastSpanIndex;

            internal TramTurnbackBuild(int firstSpanIndex, int firstEarlierSpanIndex = -1)
            {
                FirstSpanIndex = firstSpanIndex;
                FirstEarlierSpanIndex = firstEarlierSpanIndex;
                LastSpanIndex = firstSpanIndex;
            }

            internal void SetEarliestEarlierSpanIndex(int spanIndex)
            {
                if (spanIndex >= 0
                    && (FirstEarlierSpanIndex < 0 || spanIndex < FirstEarlierSpanIndex))
                {
                    FirstEarlierSpanIndex = spanIndex;
                }
            }

            internal void Extend(TramTurnbackBuild other)
            {
                LastSpanIndex = math.max(LastSpanIndex, other.LastSpanIndex);
                SetEarliestEarlierSpanIndex(other.FirstEarlierSpanIndex);
            }

            internal void SetLastEndNode(Entity node)
            {
                int index = Overlaps.Count - 1;
                if (index >= 0)
                    Overlaps[index] = Overlaps[index].WithEndNode(node);
            }
        }

        internal void BuildTurnbackBoundaries(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null)
                return;

            chain.TurnbackBoundaries.Clear();
            chain.TurnbackBuildMode = "none";
            chain.TurnbackBuildNote = string.Empty;
            chain.TurnbackBuildSegmentPairIndex = -1;
            if (chain.TrackAtoms.Count == 0)
            {
                chain.TurnbackBuildNote = "no-track-atoms";
                return;
            }

            List<int> primaryAtomIndices = CollectPrimaryAtomIndices(chain);
            if (primaryAtomIndices.Count < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS * 2)
            {
                chain.TurnbackBuildNote = "insufficient-primary-atoms";
                return;
            }

            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints, false);
            List<string> candidateNotes = new List<string>();
            for (int stationPassIndex = 0; stationPassIndex < stationPasses.Count; stationPassIndex++)
            {
                if (!TryConfirmTurnbackBoundaryAtStationPass(
                        chain,
                        stationPasses,
                        stationPassIndex,
                        out int boundaryAtomIndex,
                        out int matchedAtomCount,
                        out int matchedUniqueLaneCount,
                        out string stationNote))
                {
                    continue;
                }

                int beforeSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, math.max(0, boundaryAtomIndex - 1));
                int afterSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, boundaryAtomIndex);
                int boundaryEventIndex = ResolveTurnbackBoundaryEventIndex(chain, boundaryAtomIndex);
                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    boundaryAtomIndex,
                    beforeSliceIndex,
                    afterSliceIndex,
                    boundaryEventIndex,
                    false,
                    matchedAtomCount,
                    matchedUniqueLaneCount));
                candidateNotes.Add(stationNote);
            }

            if (chain.TurnbackBoundaries.Count > 0)
            {
                chain.TurnbackBoundaries.Sort((left, right) => left.AtomIndex.CompareTo(right.AtomIndex));
                chain.TurnbackBuildMode = "station-local-overlap";
                chain.TurnbackBuildNote = string.Join(";", candidateNotes);
                return;
            }

            if (TryFindAdjacentSegmentTurnbackBoundary(
                    chain,
                    out int adjacentBoundaryAtomIndex,
                    out int adjacentMatchedAtomCount,
                    out int adjacentMatchedUniqueLaneCount,
                    out int adjacentSegmentPairIndex,
                    out string adjacentNote))
            {
                int beforeSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, math.max(0, adjacentBoundaryAtomIndex - 1));
                int afterSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, adjacentBoundaryAtomIndex);
                int boundaryEventIndex = ResolveTurnbackBoundaryEventIndex(chain, adjacentBoundaryAtomIndex);
                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    adjacentBoundaryAtomIndex,
                    beforeSliceIndex,
                    afterSliceIndex,
                    boundaryEventIndex,
                    false,
                    adjacentMatchedAtomCount,
                    adjacentMatchedUniqueLaneCount));
                chain.TurnbackBuildMode = "adjacent-segment-fallback";
                chain.TurnbackBuildNote = adjacentNote;
                chain.TurnbackBuildSegmentPairIndex = adjacentSegmentPairIndex;
            }
            else
            {
                chain.TurnbackBuildMode = "none";
                chain.TurnbackBuildNote = "station-local-failed;adjacent-failed";
            }
        }

        internal void LogTrackModelTurnbackBuild(LineTrackChain chain)
        {
            if (!IsTurnbackBuildLoggingEnabled() || chain == null)
                return;

            List<int> primaryAtomIndices = CollectPrimaryAtomIndices(chain);
            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModelTurnbackBuild] line=").Append(chain.LineEntity.Index)
                .Append(" atoms=").Append(chain.TrackAtoms.Count)
                .Append(" primary=").Append(primaryAtomIndices.Count)
                .Append(" boundaries=").Append(chain.TurnbackBoundaries.Count)
                .Append(" mode=").Append(string.IsNullOrWhiteSpace(chain.TurnbackBuildMode) ? "none" : chain.TurnbackBuildMode)
                .Append(" note=").Append(string.IsNullOrWhiteSpace(chain.TurnbackBuildNote) ? "-" : chain.TurnbackBuildNote)
                .Append(" pair=").Append(chain.TurnbackBuildSegmentPairIndex);
            int limit = math.min(chain.TurnbackBoundaries.Count, 8);
            for (int i = 0; i < limit; i++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[i];
                sb.Append(" | tb").Append(i).Append("=atom").Append(boundary.AtomIndex)
                    .Append(boundary.IsLearned ? " learnedHits=" : " match=").Append(boundary.MatchedAtomCount)
                    .Append(boundary.IsLearned ? string.Empty : " unique=" + boundary.MatchedUniqueLaneCount)
                    .Append(" slices=").Append(boundary.BeforeSliceIndex).Append("->").Append(boundary.AfterSliceIndex);
            }
            string summary = sb.ToString();
            if (m_TrackModelTurnbackBuildLogCache.TryGetValue(chain.LineEntity, out string previousSummary)
                && previousSummary == summary)
            {
                return;
            }

            m_TrackModelTurnbackBuildLogCache[chain.LineEntity] = summary;
            log.Info(summary);
        }

        private void LogTraversalSliceCutPointsBuild(LineTrackChain chain)
        {
            if (!IsTurnbackBuildLoggingEnabled()
                || chain == null
                || chain.TraversalProfile == null
                || chain.SegmentRanges.Count == 0
                || chain.TraversalProfile.SegmentSliceCutPointProgresses == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("[TraversalSliceCutPoints] line=").Append(chain.LineEntity.Index);
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                if (segmentIndex >= chain.TraversalProfile.SegmentSliceCutPointProgresses.Length)
                    break;

                float[] cutPoints = chain.TraversalProfile.SegmentSliceCutPointProgresses[segmentIndex];
                if (cutPoints == null || cutPoints.Length == 0)
                    continue;

                TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                int segmentStartAtomIndex = segmentRange.StartAtomIndex;
                int segmentLengthAtoms = math.max(1, segmentRange.EndAtomIndexExclusive - segmentStartAtomIndex);
                sb.Append(" | seg").Append(segmentIndex).Append('=');
                for (int cutPointIndex = 0; cutPointIndex < cutPoints.Length; cutPointIndex++)
                {
                    if (cutPointIndex > 0)
                        sb.Append(',');

                    float progress = cutPoints[cutPointIndex];
                    int atomIndex = math.clamp(
                        segmentStartAtomIndex + (int)math.round(progress * segmentLengthAtoms),
                        segmentStartAtomIndex,
                        math.max(segmentStartAtomIndex, segmentRange.EndAtomIndexExclusive - 1));
                    sb.Append(progress.ToString("0.00"))
                        .Append("@a")
                        .Append(atomIndex);
                }
            }

            log.Info(sb.ToString());
        }

        private List<StationPassRange> CollectStationPassRanges(
            LineTrackChain chain,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool includeTramPasses)
        {
            List<StationPassRange> stationPasses = new List<StationPassRange>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return stationPasses;

            bool hasLinePrefabData = TryGetTraversalProfileLineData(line, out Game.Prefabs.TransportLineData prefabLineData);
            Dictionary<Entity, int> passCountByBuilding = new Dictionary<Entity, int>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count;)
            {
                Entity building = m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                {
                    atomIndex++;
                    continue;
                }

                int startAtomIndex = atomIndex;
                atomIndex++;
                while (atomIndex < chain.TrackAtoms.Count
                    && m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget) == building)
                {
                    atomIndex++;
                }

                int endAtomIndexExclusive = atomIndex;
                int waypointIndex = -1;
                float stopFrames = 0f;
                string stationId = string.Empty;
                if (TryFindTraversalStopWaypointIndex(chain, building, startAtomIndex, endAtomIndexExclusive, out int matchedWaypointIndex))
                {
                    waypointIndex = matchedWaypointIndex;
                    if (hasLinePrefabData
                        && !IsTerminalRange(
                            chain,
                            startAtomIndex,
                            endAtomIndexExclusive,
                            matchedWaypointIndex))
                    {
                        stopFrames = m_Support.GetProfileWaypointStopFrames(line, waypoints, matchedWaypointIndex, prefabLineData);
                    }
                    if (TransportModeResolver.Resolve(EntityManager, line) == TransitMode.Tram)
                        m_TramStops.TryGetStationId(line, matchedWaypointIndex, out stationId);
                }

                int passIndex = passCountByBuilding.TryGetValue(building, out int existingPassCount)
                    ? existingPassCount
                    : 0;
                passCountByBuilding[building] = passIndex + 1;

                stationPasses.Add(new StationPassRange(
                    building,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    waypointIndex,
                    stopFrames,
                    passIndex,
                    stationId));
            }

            if (TransportModeResolver.Resolve(EntityManager, line) == TransitMode.Tram)
            {
                MergeTramStationRanges(chain, stationPasses);
                int originalRangeCount = stationPasses.Count;
                AppendTramCurrentStops(
                    chain,
                    line,
                    waypoints,
                    stationPasses,
                    originalRangeCount,
                    hasLinePrefabData,
                    prefabLineData);
                if (includeTramPasses)
                {
                    AppendTramPasses(chain, line, stationPasses, originalRangeCount);
                }

                stationPasses.Sort(CompareTramRanges);
                ReassignTramPassIndices(stationPasses);
            }

            return stationPasses;
        }

        private void MergeTramStationRanges(
            LineTrackChain chain,
            List<StationPassRange> stationPasses)
        {
            for (int index = 0; index + 1 < stationPasses.Count;)
            {
                StationPassRange left = stationPasses[index];
                StationPassRange right = stationPasses[index + 1];
                if (!CanMergeTramStationRanges(chain, left, right))
                {
                    index++;
                    continue;
                }

                StationPassRange stop = left.WaypointIndex >= 0 ? left : right;
                stationPasses[index] = new StationPassRange(
                    left.Building,
                    left.StartAtomIndex,
                    right.EndAtomIndexExclusive,
                    stop.WaypointIndex,
                    stop.StopFrames,
                    left.PassIndex,
                    stop.StationId);
                stationPasses.RemoveAt(index + 1);
            }
        }

        private bool CanMergeTramStationRanges(
            LineTrackChain chain,
            StationPassRange left,
            StationPassRange right)
        {
            if (left.Building == Entity.Null
                || left.Building != right.Building
                || (left.WaypointIndex < 0 && right.WaypointIndex < 0)
                || (left.WaypointIndex >= 0
                    && right.WaypointIndex >= 0
                    && left.WaypointIndex != right.WaypointIndex)
                || right.StartAtomIndex < left.EndAtomIndexExclusive
                || right.StartAtomIndex - left.EndAtomIndexExclusive > 1)
            {
                return false;
            }

            for (int atomIndex = left.EndAtomIndexExclusive;
                atomIndex < right.StartAtomIndex;
                atomIndex++)
            {
                if (m_Support.ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget) != Entity.Null)
                    return false;
            }

            return TryGetStationRangeDirection(chain, left, out TrackTraversalDir leftDirection)
                && TryGetStationRangeDirection(chain, right, out TrackTraversalDir rightDirection)
                && leftDirection == rightDirection;
        }

        private static bool TryGetStationRangeDirection(
            LineTrackChain chain,
            StationPassRange range,
            out TrackTraversalDir direction)
        {
            direction = TrackTraversalDir.Unknown;
            for (int atomIndex = range.StartAtomIndex;
                atomIndex < range.EndAtomIndexExclusive;
                atomIndex++)
            {
                TrackTraversalDir current = chain.TrackAtoms[atomIndex].TraversalDir;
                if (current == TrackTraversalDir.Unknown)
                    return false;
                if (direction == TrackTraversalDir.Unknown)
                    direction = current;
                else if (direction != current)
                    return false;
            }

            return direction != TrackTraversalDir.Unknown;
        }

        private void AppendTramCurrentStops(
            LineTrackChain chain,
            Entity currentLine,
            DynamicBuffer<RouteWaypoint> waypoints,
            List<StationPassRange> stationPasses,
            int originalRangeCount,
            bool hasLinePrefabData,
            Game.Prefabs.TransportLineData prefabLineData)
        {
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                Entity stop = m_Support.Stop(waypoints[waypointIndex].m_Waypoint);
                if (!IsStopOnly(stop, waypoints, waypointIndex)
                    || !TryFindStopControlPoint(chain, waypointIndex, stop, out int atomIndex))
                {
                    continue;
                }

                if (IsCoveredByRanges(stationPasses, originalRangeCount, atomIndex)
                    || HasStopRange(stationPasses, stop, atomIndex)
                    || atomIndex < 0
                    || atomIndex >= chain.TrackAtoms.Count)
                {
                    continue;
                }

                float stopFrames = hasLinePrefabData
                    ? m_Support.GetProfileWaypointStopFrames(currentLine, waypoints, waypointIndex, prefabLineData)
                    : 0f;
                m_TramStops.TryGetStationId(currentLine, waypointIndex, out string stationId);
                stationPasses.Add(new StationPassRange(
                    stop,
                    atomIndex,
                    atomIndex + 1,
                    waypointIndex,
                    stopFrames,
                    0,
                    stationId));
            }
        }

        private void AppendTramPasses(
            LineTrackChain chain,
            Entity currentLine,
            List<StationPassRange> stationPasses,
            int originalRangeCount)
        {
            var candidates = new List<TramPassRange>();
            m_TramStops.CollectPasses(currentLine, chain, candidates);
            var stationByAtom = new Dictionary<int, string>();
            var ambiguousAtoms = new HashSet<int>();
            for (int i = 0; i < candidates.Count; i++)
            {
                TramPassRange candidate = candidates[i];
                if (candidate.AtomIndex < 0
                    || candidate.AtomIndex >= chain.TrackAtoms.Count
                    || string.IsNullOrEmpty(candidate.StationId)
                    || IsCoveredByRanges(stationPasses, originalRangeCount, candidate.AtomIndex)
                    || HasCurrentStop(stationPasses, candidate.AtomIndex)
                    || ambiguousAtoms.Contains(candidate.AtomIndex))
                {
                    continue;
                }

                if (stationByAtom.TryGetValue(candidate.AtomIndex, out string stationId))
                {
                    if (!string.Equals(stationId, candidate.StationId, StringComparison.Ordinal))
                    {
                        RemoveProjectedPasses(stationPasses, originalRangeCount, candidate.AtomIndex);
                        ambiguousAtoms.Add(candidate.AtomIndex);
                        stationPasses.Add(new StationPassRange(
                            Entity.Null,
                            candidate.AtomIndex,
                            candidate.AtomIndex,
                            -1,
                            0f,
                            0,
                            string.Empty,
                            true));
                    }
                    continue;
                }

                stationByAtom[candidate.AtomIndex] = candidate.StationId;
                stationPasses.Add(new StationPassRange(
                    candidate.Stop,
                    candidate.AtomIndex,
                    candidate.AtomIndex + 1,
                    -1,
                    0f,
                    0,
                    candidate.StationId));
            }
        }

        private bool IsStopOnly(
            Entity stop,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex)
        {
            return stop != Entity.Null
                && EntityManager.HasComponent<TransportStop>(stop)
                && m_Support.StationOf(stop) == Entity.Null
                && m_Support.GetStationBuildingForWaypoint(waypoints, waypointIndex) == Entity.Null;
        }

        private bool TryFindStopControlPoint(
            LineTrackChain chain,
            int waypointIndex,
            Entity stop,
            out int atomIndex)
        {
            atomIndex = -1;
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker marker = chain.ControlPoints[i];
                if (marker.WaypointIndex == waypointIndex
                    && marker.Kind == ControlPointKind.Stop
                    && marker.Building == stop)
                {
                    atomIndex = marker.AtomIndex;
                    return true;
                }
            }

            return false;
        }

        private void ReassignTramPassIndices(List<StationPassRange> stationPasses)
        {
            Dictionary<Entity, int> passCounts = new Dictionary<Entity, int>();
            for (int i = 0; i < stationPasses.Count; i++)
            {
                StationPassRange range = stationPasses[i];
                if (!EntityManager.HasComponent<TransportStop>(range.Building))
                    continue;

                int passIndex = passCounts.TryGetValue(range.Building, out int count) ? count : 0;
                passCounts[range.Building] = passIndex + 1;
                stationPasses[i] = new StationPassRange(
                    range.Building,
                    range.StartAtomIndex,
                    range.EndAtomIndexExclusive,
                    range.WaypointIndex,
                    range.StopFrames,
                    passIndex,
                    range.StationId,
                    range.IsBreak);
            }
        }

        private static bool IsCoveredByRanges(
            List<StationPassRange> stationPasses,
            int rangeCount,
            int atomIndex)
        {
            int count = math.min(rangeCount, stationPasses.Count);
            for (int i = 0; i < count; i++)
            {
                StationPassRange range = stationPasses[i];
                if (atomIndex >= range.StartAtomIndex
                    && atomIndex < range.EndAtomIndexExclusive)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasStopRange(
            List<StationPassRange> stationPasses,
            Entity stop,
            int atomIndex)
        {
            for (int i = 0; i < stationPasses.Count; i++)
            {
                StationPassRange range = stationPasses[i];
                if (range.Building == stop
                    && range.StartAtomIndex == atomIndex
                    && range.EndAtomIndexExclusive == atomIndex + 1)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasCurrentStop(List<StationPassRange> stationPasses, int atomIndex)
        {
            for (int i = 0; i < stationPasses.Count; i++)
            {
                StationPassRange range = stationPasses[i];
                if (range.WaypointIndex >= 0
                    && range.StartAtomIndex == atomIndex
                    && range.EndAtomIndexExclusive == atomIndex + 1
                    && EntityManager.HasComponent<TransportStop>(range.Building))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveProjectedPasses(
            List<StationPassRange> stationPasses,
            int originalRangeCount,
            int atomIndex)
        {
            for (int i = stationPasses.Count - 1; i >= originalRangeCount; i--)
            {
                StationPassRange range = stationPasses[i];
                if (range.WaypointIndex < 0
                    && range.StartAtomIndex == atomIndex
                    && range.EndAtomIndexExclusive == atomIndex + 1)
                {
                    stationPasses.RemoveAt(i);
                }
            }
        }

        private static int CompareTramRanges(StationPassRange left, StationPassRange right)
        {
            int compare = left.StartAtomIndex.CompareTo(right.StartAtomIndex);
            if (compare != 0)
                return compare;

            compare = left.EndAtomIndexExclusive.CompareTo(right.EndAtomIndexExclusive);
            if (compare != 0)
                return compare;

            bool leftStop = left.WaypointIndex >= 0;
            bool rightStop = right.WaypointIndex >= 0;
            if (leftStop != rightStop)
                return leftStop ? -1 : 1;

            compare = left.WaypointIndex.CompareTo(right.WaypointIndex);
            if (compare != 0)
                return compare;

            compare = StringComparer.Ordinal.Compare(left.StationId, right.StationId);
            return compare != 0 ? compare : left.PassIndex.CompareTo(right.PassIndex);
        }

        private bool TryGetTraversalProfileLineData(Entity line, out Game.Prefabs.TransportLineData prefabLineData)
        {
            prefabLineData = default;
            if (line == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(line))
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<Game.Prefabs.TransportLineData>(prefab))
                return false;

            prefabLineData = EntityManager.GetComponentData<Game.Prefabs.TransportLineData>(prefab);
            return true;
        }

        private bool TryFindTraversalStopWaypointIndex(
            LineTrackChain chain,
            Entity building,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (chain == null || building == Entity.Null)
                return false;

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
            {
                ControlPointMarker marker = chain.ControlPoints[controlPointIndex];
                if ((marker.Kind != ControlPointKind.Stop && marker.Kind != ControlPointKind.Bypass)
                    || marker.Building != building
                    || marker.AtomIndex < startAtomIndex
                    || marker.AtomIndex >= endAtomIndexExclusive)
                {
                    continue;
                }

                waypointIndex = marker.WaypointIndex;
                return true;
            }

            if (!chain.ChainComplete
                || startAtomIndex <= 0
                || endAtomIndexExclusive != chain.TrackAtoms.Count)
            {
                return false;
            }

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
            {
                ControlPointMarker marker = chain.ControlPoints[controlPointIndex];
                if (marker.AtomIndex != 0
                    || marker.WaypointIndex != 0
                    || marker.Building != building
                    || (marker.Kind != ControlPointKind.Stop && marker.Kind != ControlPointKind.Bypass))
                {
                    continue;
                }

                waypointIndex = marker.WaypointIndex;
                return true;
            }

            return false;
        }

        private static bool IsTerminalRange(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int waypointIndex)
        {
            return chain != null
                && chain.ChainComplete
                && waypointIndex == 0
                && startAtomIndex > 0
                && endAtomIndexExclusive == chain.TrackAtoms.Count;
        }

        private static Entity[] CollectTraversalSlicePhysicalLaneKeys(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return Array.Empty<Entity>();

            HashSet<Entity> seenKeys = new HashSet<Entity>();
            List<Entity> orderedKeys = new List<Entity>();
            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return Array.Empty<Entity>();

            void AppendRange(int rangeStart, int rangeEndExclusive)
            {
                for (int atomIndex = rangeStart; atomIndex < rangeEndExclusive; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (atom.Key.PhysicalLaneKey != Entity.Null && seenKeys.Add(atom.Key.PhysicalLaneKey))
                        orderedKeys.Add(atom.Key.PhysicalLaneKey);
                }
            }

            if (start < end)
                AppendRange(start, end);
            else
            {
                AppendRange(start, atomCount);
                AppendRange(0, end);
            }

            if (orderedKeys.Count == 0)
                return Array.Empty<Entity>();

            return orderedKeys.ToArray();
        }

        private Entity ResolveTrackContainerKey(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Game.Net.Edge>(current)
                    || EntityManager.HasComponent<Game.Net.Node>(current)
                    || EntityManager.HasBuffer<Game.Net.SubLane>(current))
                {
                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return Entity.Null;
        }

        private Entity[] CollectTraversalSliceTrackContainerSequenceCircular(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return Array.Empty<Entity>();

            List<Entity> orderedKeys = new List<Entity>();
            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return Array.Empty<Entity>();

            void AppendRange(int rangeStart, int rangeEndExclusive)
            {
                for (int atomIndex = rangeStart; atomIndex < rangeEndExclusive; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    Entity containerKey = ResolveTrackContainerKey(atom.Key.PhysicalLaneKey);
                    if (containerKey != Entity.Null)
                        orderedKeys.Add(containerKey);
                }
            }

            if (start < end)
                AppendRange(start, end);
            else
            {
                AppendRange(start, atomCount);
                AppendRange(0, end);
            }

            if (orderedKeys.Count == 0)
                return Array.Empty<Entity>();

            return orderedKeys.ToArray();
        }

        private float EstimateTraversalRunSliceFrames(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            bool hasTimeProfile,
            LineTimeProfileHeader timeProfile,
            float lineFrames)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return 0f;
            }

            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);

            float atomCount = math.max(1f, chain.TrackAtoms.Count);
            return lineFrames > 0f
                ? lineFrames * ((endAtomIndexExclusive - startAtomIndex) / atomCount)
                : 0f;
        }

        private static bool IsTurnbackBuildLoggingEnabled() => RtLog.VerboseEnabled;

        internal void ClearAll()
        {
            m_TrackModelTurnbackBuildLogCache.Clear();
        }

        internal void ClearLine(Entity line)
        {
            if (line != Entity.Null)
                m_TrackModelTurnbackBuildLogCache.Remove(line);
        }


    }
}
