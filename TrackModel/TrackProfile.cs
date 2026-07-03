using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
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
        private readonly Dictionary<Entity, string> m_TrackModelTurnbackBuildLogCache = new Dictionary<Entity, string>();

        internal TrackProfile(TrackSupport support)
        {
            m_Support = support;
        }

        private EntityManager EntityManager => m_Support.EntityManager;
        private TimedLogger log => m_Support.Log;

        private readonly struct StationPassRange
        {
            public readonly Entity Building;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly int WaypointIndex;
            public readonly float StopFrames;
            public readonly int PassIndex;

            public StationPassRange(
                Entity building,
                int startAtomIndex,
                int endAtomIndexExclusive,
                int waypointIndex,
                float stopFrames,
                int passIndex)
            {
                Building = building;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                WaypointIndex = waypointIndex;
                StopFrames = stopFrames;
                PassIndex = passIndex;
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
            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints);
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

                int approachIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    approachIndex,
                    TraversalEventKind.ApproachSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.StartAtomIndex,
                    0f));
                boundaryEventIndexByAtom[pass.StartAtomIndex] = approachIndex;

                int stationEventIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    stationEventIndex,
                    pass.WaypointIndex >= 0 ? TraversalEventKind.Stop : TraversalEventKind.Pass,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.EndAtomIndexExclusive,
                    pass.StopFrames));

                int departureIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    departureIndex,
                    TraversalEventKind.DepartureSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
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

            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints);
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
            DynamicBuffer<RouteWaypoint> waypoints)
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
                int passIndex = passCountByBuilding.TryGetValue(building, out int existingPassCount)
                    ? existingPassCount
                    : 0;
                passCountByBuilding[building] = passIndex + 1;

                int waypointIndex = -1;
                float stopFrames = 0f;
                if (TryFindTraversalStopWaypointIndex(chain, building, startAtomIndex, endAtomIndexExclusive, out int matchedWaypointIndex))
                {
                    waypointIndex = matchedWaypointIndex;
                    if (hasLinePrefabData)
                        stopFrames = m_Support.GetProfileWaypointStopFrames(line, waypoints, matchedWaypointIndex, prefabLineData);
                }

                stationPasses.Add(new StationPassRange(
                    building,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    waypointIndex,
                    stopFrames,
                    passIndex));
            }

            return stationPasses;
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

            return false;
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
