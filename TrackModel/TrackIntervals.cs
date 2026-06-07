using System;
using System.Collections.Generic;
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
    internal sealed class TrackIntervals
    {
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;
        private readonly TrackSupport m_Support;
        private readonly SceneCache m_Scene;
        private readonly SharedIndex m_Shared;
        private readonly TrackBuild m_Build;

        internal TrackIntervals(TrackSupport support, SceneCache scene, SharedIndex shared, TrackBuild build)
        {
            m_Support = support;
            m_Scene = scene;
            m_Shared = shared;
            m_Build = build;
        }

        internal void EnsureBypassPipelineReady(LineTrackChain chain)
        {
            if (chain == null)
                return;

            m_Shared.EnsureSharedTrackIndexCurrent();
            if (chain.BypassPipelineReadyVersion == m_Shared.Version
                && chain.ProtectedIntervalSummariesReady)
            {
                return;
            }

            m_Shared.RefreshSharedRuns(chain);
            RefreshControlEdgeSharedSpans(chain);
            RefreshBypassProtectedIntervals(chain);
            RefreshProtectedSharedIntervals(chain);
            RefreshProtectedIntervalSummaries(chain);

            chain.BypassPipelineReadyVersion =
                chain.SharedRunsVersion == m_Shared.Version
                && chain.ControlEdgeSharedSpansReady
                && chain.BypassProtectedIntervalsReady
                && chain.ProtectedSharedIntervalsReady
                && chain.ProtectedIntervalSummariesReady
                    ? m_Shared.Version
                    : 0;
        }

        internal void EnsureBypassPipelineReady(LineTrackChain chain, ModeScope scope)
        {
            if (chain == null)
                return;

            TrackModelBuilder scopedIndex = new TrackModelBuilder();
            uint scopedVersion = m_Shared.BuildScopedSharedTrackIndex(scope, scopedIndex);
            ResetBypassPipeline(chain);
            m_Shared.RefreshSharedRuns(chain, scopedIndex, scopedVersion);
            RefreshControlEdgeSharedSpans(chain);
            RefreshBypassProtectedIntervals(chain);
            RefreshProtectedSharedIntervals(chain);
            RefreshProtectedIntervalSummaries(chain);

            chain.BypassPipelineReadyVersion =
                chain.SharedRunsVersion == scopedVersion
                && chain.ControlEdgeSharedSpansReady
                && chain.BypassProtectedIntervalsReady
                && chain.ProtectedSharedIntervalsReady
                && chain.ProtectedIntervalSummariesReady
                    ? scopedVersion
                    : 0;
        }

        internal static void ResetBypassPipeline(LineTrackChain chain)
        {
            if (chain == null)
                return;

            chain.SharedRuns.Clear();
            chain.SharedRunsByOtherLine.Clear();
            chain.ControlEdgeSharedSpans.Clear();
            chain.BypassProtectedIntervals.Clear();
            chain.ProtectedSharedIntervals.Clear();
            chain.ProtectedIntervalSummaries.Clear();
            chain.SharedRunsVersion = 0;
            chain.BypassPipelineReadyVersion = 0;
            chain.ControlEdgeSharedSpansReady = false;
            chain.BypassProtectedIntervalsReady = false;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
        }

        private void EnsureLocalBypassWaypointScenesReady(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null || waypoints.Length == 0)
                return;

            if (chain.LocalBypassWaypointScenesVersion == chain.BypassPipelineReadyVersion
                && chain.LocalBypassWaypointScenesVersion != 0
                && chain.LocalBypassWaypointScenes != null
                && chain.LocalBypassWaypointScenes.Length == waypoints.Length)
            {
                return;
            }

            LocalBypassWaypointSceneBinding[] bindings = new LocalBypassWaypointSceneBinding[waypoints.Length];
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!m_Support.TryGetBypassWaypointContext(
                        waypoints,
                        waypointIndex,
                        out Entity currentBypassBuilding,
                        out _,
                        out Entity nextBypassBuilding))
                {
                    continue;
                }

                if (!TryResolveBypassProtectedInterval(
                        chain,
                        waypoints,
                        waypointIndex,
                        out int protectedIntervalIndex,
                        out BypassProtectedInterval protectedInterval)
                    || protectedIntervalIndex < 0
                    || protectedIntervalIndex >= chain.ProtectedIntervalSummaries.Count)
                {
                    continue;
                }

                ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[protectedIntervalIndex];
                float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(chain, protectedInterval, currentBypassBuilding);
                float intervalDisplayLength = GetProtectedIntervalDisplayLength(protectedInterval);
                SceneKey sceneKey = new SceneKey(
                    chain.LineEntity,
                    currentBypassBuilding,
                    nextBypassBuilding,
                    protectedIntervalIndex);
                bindings[waypointIndex] = new LocalBypassWaypointSceneBinding(
                    true,
                    sceneKey,
                    currentBypassBuilding,
                    nextBypassBuilding,
                    protectedIntervalIndex,
                    protectedInterval,
                    summary,
                    departureReleaseCoordinate,
                    intervalDisplayLength);
            }

            chain.LocalBypassWaypointScenes = bindings;
            chain.LocalBypassWaypointScenesVersion = chain.BypassPipelineReadyVersion;
        }

        private void RefreshControlEdgeSharedSpans(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdgeSharedSpansReady)
                return;

            chain.ControlEdgeSharedSpans.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.SharedRuns.Count == 0 || chain.ControlEdges.Count == 0)
            {
                chain.ControlEdgeSharedSpansReady = true;
                return;
            }

            for (int controlEdgeIndex = 0; controlEdgeIndex < chain.ControlEdges.Count; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                for (int runIndex = 0; runIndex < chain.SharedRuns.Count; runIndex++)
                {
                    SharedTrackRun run = chain.SharedRuns[runIndex];
                    int overlapStart = math.max(edge.StartAtomIndex, run.StartAtomIndex);
                    int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, run.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    chain.ControlEdgeSharedSpans.Add(new ControlEdgeSharedSpan(
                        controlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        run.HasMirroredContext,
                        run.SharedLineCount));
                }
            }

            chain.ControlEdgeSharedSpansReady = true;
        }

        private void RefreshBypassProtectedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.BypassProtectedIntervalsReady)
                return;

            chain.BypassProtectedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.ControlPoints.Count < 2 || chain.ControlEdges.Count == 0)
            {
                chain.BypassProtectedIntervalsReady = true;
                return;
            }

            for (int startControlPointIndex = 0; startControlPointIndex < chain.ControlPoints.Count - 1; startControlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[startControlPointIndex];
                if (start.Kind != ControlPointKind.Bypass)
                    continue;

                int endControlPointIndex = -1;
                for (int candidateIndex = startControlPointIndex + 1; candidateIndex < chain.ControlPoints.Count; candidateIndex++)
                {
                    if (chain.ControlPoints[candidateIndex].Kind == ControlPointKind.Bypass)
                    {
                        endControlPointIndex = candidateIndex;
                        break;
                    }
                }

                if (endControlPointIndex <= startControlPointIndex)
                    continue;

                int startControlEdgeIndex = startControlPointIndex;
                int endControlEdgeIndexInclusive = endControlPointIndex - 1;
                if (startControlEdgeIndex < 0 || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
                    continue;

                int startAtomIndex = math.max(0, chain.ControlPoints[startControlPointIndex].AtomIndex);
                int endAtomIndexExclusive = math.max(startAtomIndex + 1, chain.ControlPoints[endControlPointIndex].AtomIndex);
                float baseFrames = 0f;
                for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
                    baseFrames += chain.ControlEdges[controlEdgeIndex].BaseFrames;

                chain.BypassProtectedIntervals.Add(new BypassProtectedInterval(
                    startControlPointIndex,
                    endControlPointIndex,
                    startControlEdgeIndex,
                    endControlEdgeIndexInclusive,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }

            chain.BypassProtectedIntervalsReady = true;
        }

        private void RefreshProtectedSharedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedSharedIntervalsReady)
                return;

            chain.ProtectedSharedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.BypassProtectedIntervals.Count == 0 || chain.ControlEdgeSharedSpans.Count == 0)
            {
                chain.ProtectedSharedIntervalsReady = true;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                BypassProtectedInterval interval = chain.BypassProtectedIntervals[protectedIntervalIndex];
                for (int spanIndex = 0; spanIndex < chain.ControlEdgeSharedSpans.Count; spanIndex++)
                {
                    ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[spanIndex];
                    if (span.ControlEdgeIndex < interval.StartControlEdgeIndex || span.ControlEdgeIndex > interval.EndControlEdgeIndexInclusive)
                        continue;

                    int overlapStart = math.max(interval.StartAtomIndex, span.StartAtomIndex);
                    int overlapEndExclusive = math.min(interval.EndAtomIndexExclusive, span.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    float entryOffsetFrames = EstimateFramesBetweenAtoms(chain, interval.StartControlEdgeIndex, span.ControlEdgeIndex, interval.StartAtomIndex, overlapStart);
                    float clearOffsetFrames = EstimateFramesBetweenAtoms(chain, span.ControlEdgeIndex, interval.EndControlEdgeIndexInclusive, overlapEndExclusive, interval.EndAtomIndexExclusive);
                    chain.ProtectedSharedIntervals.Add(new ProtectedSharedInterval(
                        protectedIntervalIndex,
                        span.ControlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        span.HasMirroredContext,
                        span.SharedLineCount,
                        entryOffsetFrames,
                        clearOffsetFrames));
                }
            }

            chain.ProtectedSharedIntervalsReady = true;
        }

        private void RefreshProtectedIntervalSummaries(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedIntervalSummariesReady)
                return;

            chain.ProtectedIntervalSummaries.Clear();
            chain.BypassPipelineReadyVersion = 0;
            if (chain.BypassProtectedIntervals.Count == 0)
            {
                chain.ProtectedIntervalSummariesReady = true;
                chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_Shared.Version ? m_Shared.Version : 0;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                int sharedSegmentCount = 0;
                int maxSharedLineCount = 0;
                bool hasMirroredContext = false;
                float minEntryOffsetFrames = float.MaxValue;
                float maxClearOffsetFrames = 0f;

                for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
                {
                    ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                    if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                        continue;

                    sharedSegmentCount++;
                    maxSharedLineCount = math.max(maxSharedLineCount, interval.SharedLineCount);
                    hasMirroredContext |= interval.HasMirroredContext;
                    minEntryOffsetFrames = math.min(minEntryOffsetFrames, interval.EntryOffsetFrames);
                    maxClearOffsetFrames = math.max(maxClearOffsetFrames, interval.ClearOffsetFrames);
                }

                if (sharedSegmentCount == 0)
                {
                    minEntryOffsetFrames = 0f;
                    maxClearOffsetFrames = 0f;
                }

                chain.ProtectedIntervalSummaries.Add(new ProtectedIntervalSummary(
                    protectedIntervalIndex,
                    sharedSegmentCount,
                    maxSharedLineCount,
                    hasMirroredContext,
                    minEntryOffsetFrames,
                    maxClearOffsetFrames));
            }

            chain.ProtectedIntervalSummariesReady = true;
            chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_Shared.Version ? m_Shared.Version : 0;
        }

        internal float EstimateFramesBetweenAtoms(LineTrackChain chain, int startControlEdgeIndex, int endControlEdgeIndexInclusive, int fromAtomIndex, int toAtomIndexExclusive)
        {
            if (toAtomIndexExclusive <= fromAtomIndex
                || startControlEdgeIndex < 0
                || endControlEdgeIndexInclusive < startControlEdgeIndex
                || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
            {
                return 0f;
            }

            float frames = 0f;
            for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                int overlapStart = math.max(edge.StartAtomIndex, fromAtomIndex);
                int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, toAtomIndexExclusive);
                if (overlapEndExclusive <= overlapStart)
                    continue;

                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                int overlapAtomLength = overlapEndExclusive - overlapStart;
                frames += edge.BaseFrames * (overlapAtomLength / (float)edgeAtomLength);
            }

            return frames;
        }

        internal static float EstimateAverageControlEdgeFramesPerAtom(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdges == null || chain.ControlEdges.Count == 0)
                return 0f;

            float totalFrames = 0f;
            int totalAtoms = 0;
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                totalFrames += edge.BaseFrames;
                totalAtoms += edgeAtomLength;
            }

            return totalAtoms > 0 ? totalFrames / totalAtoms : 0f;
        }

        internal bool TryResolveBypassProtectedInterval(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null || waypoints.Length == 0)
                return false;

            if (!m_Support.TryGetBypassWaypointContext(
                    waypoints,
                    currentWaypointIndex,
                    out Entity currentBypassBuilding,
                    out _,
                    out Entity nextBypassBuilding))
            {
                return false;
            }

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                int startWaypointIndex = chain.ControlPoints[candidate.StartControlPointIndex].WaypointIndex;
                int endWaypointIndex = chain.ControlPoints[candidate.EndControlPointIndex].WaypointIndex;
                Entity startBuilding = chain.ControlPoints[candidate.StartControlPointIndex].Building;
                Entity endBuilding = chain.ControlPoints[candidate.EndControlPointIndex].Building;
                if (startWaypointIndex != currentWaypointIndex
                    || startBuilding != currentBypassBuilding
                    || endBuilding != nextBypassBuilding)
                {
                    continue;
                }

                protectedIntervalIndex = i;
                protectedInterval = candidate;
                return true;
            }

            return false;
        }

        internal bool TryGetLocalSceneSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out LineTrackChain chain,
            out LocalBypassSceneStaticSnapshot snapshot)
        {
            chain = null;
            snapshot = default;
            if (line == Entity.Null
                || currentWaypointIndex < 0
                || currentWaypointIndex >= waypoints.Length
                || !m_Build.TryGetLineTrackChain(line, waypoints, out chain))
            {
                return false;
            }

            EnsureBypassPipelineReady(chain);
            EnsureLocalBypassWaypointScenesReady(chain, waypoints);
            LocalBypassSceneStaticKey key = new LocalBypassSceneStaticKey(line, currentWaypointIndex);
            if (m_Scene.TryGetStaticSceneSnapshot(key, out snapshot)
                && snapshot.LineChainSignature == chain.Signature)
            {
                return true;
            }

            if (chain.LocalBypassWaypointScenes == null
                || currentWaypointIndex < 0
                || currentWaypointIndex >= chain.LocalBypassWaypointScenes.Length)
            {
                return false;
            }

            LocalBypassWaypointSceneBinding binding = chain.LocalBypassWaypointScenes[currentWaypointIndex];
            if (!binding.Available)
                return false;

            snapshot = new LocalBypassSceneStaticSnapshot(
                binding.SceneKey,
                chain.Signature,
                binding.CurrentBypassBuilding,
                binding.NextBypassBuilding,
                binding.ProtectedIntervalIndex,
                binding.ProtectedInterval,
                binding.Summary,
                binding.DepartureReleaseCoordinate,
                binding.IntervalDisplayLength);
            m_Scene.PutStaticSceneSnapshot(key, snapshot);
            return true;
        }

        internal bool TryGetLocalScene(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out LineTrackChain chain,
            out SceneDefinition scene)
        {
            chain = null;
            scene = default;
            if (!TryGetLocalSceneSnapshot(
                    line,
                    waypoints,
                    currentWaypointIndex,
                    out chain,
                    out LocalBypassSceneStaticSnapshot snapshot))
            {
                return false;
            }

            scene = new SceneDefinition(
                snapshot.SceneKey,
                line,
                currentWaypointIndex,
                snapshot.CurrentBypassBuilding,
                snapshot.NextBypassBuilding,
                snapshot.ProtectedIntervalIndex,
                snapshot.ProtectedInterval,
                snapshot.Summary,
                snapshot.DepartureReleaseCoordinate,
                snapshot.IntervalDisplayLength);
            return true;
        }

        private static int FindControlPointIndex(LineTrackChain chain, int waypointIndex, Entity building, ControlPointKind kind)
        {
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker marker = chain.ControlPoints[i];
                if (marker.Kind == kind
                    && marker.WaypointIndex == waypointIndex
                    && marker.Building == building)
                {
                    return i;
                }
            }

            return -1;
        }

        internal int CountProtectedSharedIntervals(LineTrackChain chain, int protectedIntervalIndex, out bool hasMirroredContext)
        {
            hasMirroredContext = false;
            if (protectedIntervalIndex < 0)
                return 0;

            int count = 0;
            for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
            {
                ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                    continue;

                count++;
                hasMirroredContext |= interval.HasMirroredContext;
            }

            return count;
        }

        internal static string FormatProtectedIntervalSummary(ProtectedIntervalSummary summary, BypassProtectedInterval interval)
        {
            return "trackModel[p=" + summary.ProtectedIntervalIndex
                + " cp=" + interval.StartControlPointIndex + "->" + interval.EndControlPointIndex
                + " edges=" + interval.StartControlEdgeIndex + ".." + interval.EndControlEdgeIndexInclusive
                + " shared=" + summary.SharedSegmentCount
                + " maxSharedLines=" + summary.MaxSharedLineCount
                + " mirrored=" + (summary.HasMirroredContext ? "1" : "0")
                + " minEntry=" + summary.MinEntryOffsetFrames.ToString("F1")
                + " maxClear=" + summary.MaxClearOffsetFrames.ToString("F1")
                + "]";
        }

        internal static string ClassifyProtectedIntervalTrackModelRisk(ProtectedIntervalSummary summary)
        {
            if (summary.SharedSegmentCount <= 0)
                return "none";

            if (summary.HasMirroredContext)
                return "mirrored-shared";

            return "shared";
        }

        internal bool TryGetStationExitCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out float stationExitCoordinate)
        {
            stationExitCoordinate = -1f;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            int lastForwardStationAtomIndex = -1;
            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = m_Support.ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                lastForwardStationAtomIndex = atomIndex;
            }

            if (lastForwardStationAtomIndex < localProtectedInterval.StartAtomIndex)
                return false;

            stationExitCoordinate = (lastForwardStationAtomIndex - localProtectedInterval.StartAtomIndex) + 1f;
            return true;
        }

        internal bool TryGetStationExitAtom(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out int stationExitAtomIndex)
        {
            stationExitAtomIndex = -1;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = m_Support.ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                stationExitAtomIndex = atomIndex;
            }

            return stationExitAtomIndex >= localProtectedInterval.StartAtomIndex;
        }

        private float ComputeForwardDepartureReleaseCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding)
        {
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(localProtectedInterval);
            float fallback = math.min(intervalDisplayLength, LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
            if (!TryGetStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return fallback;

            return math.min(intervalDisplayLength, stationExitCoordinate + LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
        }

        internal static float GetProtectedIntervalDisplayLength(BypassProtectedInterval interval)
        {
            return math.max(1f, interval.EndAtomIndexExclusive - interval.StartAtomIndex);
        }

        internal static float MapControlPointToProtectedIntervalCoordinate(LineTrackChain chain, BypassProtectedInterval interval, int controlPointIndex)
        {
            if (chain == null
                || controlPointIndex < 0
                || controlPointIndex >= chain.ControlPoints.Count)
            {
                return 0f;
            }

            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            int atomIndex = chain.ControlPoints[controlPointIndex].AtomIndex;
            return math.clamp(atomIndex - interval.StartAtomIndex, 0f, intervalLength);
        }
    }
}
