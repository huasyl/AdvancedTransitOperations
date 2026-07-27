using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Bypass;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackModelService
    {
        private readonly TrackSupport m_Support;
        private readonly TrackState m_State;
        private readonly SceneCache m_Scene;
        private readonly TrackBuild m_Build;
        private readonly TrackProfile m_Profile;
        private readonly TrackIntervals m_Intervals;
        private readonly SharedIndex m_Shared;
        private readonly TrackQuery m_Query;
        private readonly TrackDiag m_Diag;
        private readonly TrackDump m_Dump;

        internal TrackModelService(ITrackModelRuntimeContext runtime)
        {
            m_Support = new TrackSupport(runtime);
            m_State = new TrackState();
            m_Scene = new SceneCache();
            m_Diag = new TrackDiag(m_Support);
            m_Profile = new TrackProfile(m_Support);
            m_Build = new TrackBuild(m_State, m_Support, m_Profile, m_Diag, () => m_Shared.MarkDirty(), runtime.NotifyLineTrackChainRebuilt);
            m_Shared = new SharedIndex(m_Support, m_Build);
            m_Intervals = new TrackIntervals(m_Support, m_Scene, m_Shared, m_Build);
            m_Query = new TrackQuery(m_State, m_Shared, m_Support);
            m_Diag.Bind(m_Build, m_Shared, m_Intervals);
            m_Dump = new TrackDump(m_Support, m_Build, m_Shared, m_Intervals, m_Diag);
        }

        internal uint SharedIndexVersion => m_Shared.Version;
        internal void MarkSharedIndexDirty() => m_Shared.MarkDirty();
        internal void Dispose() { }

        internal void InvalidateLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_State.MarkDirty(line);
            m_Shared.MarkDirty();
            if (m_State.RemoveLine(line, out LineTrackChain existingChain) && existingChain != null)
                m_Diag.RemoveDevSightChain(existingChain);

            m_Scene.ClearLine(line);
            m_Profile.ClearLine(line);
        }

        internal void InvalidateWaypointIndexLookup(Entity line)
        {
            m_State.RemoveWaypointLookup(line);
        }

        internal void InvalidateAll()
        {
            m_State.ClearLines();
            m_Shared.Clear();
            m_Scene.ClearAll();
            m_Profile.ClearAll();
            m_Diag.ClearAll();
        }

        internal bool TryGetChainForLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
            => m_Build.TryGetLineTrackChain(line, waypoints, out chain);

        internal bool TryChain(Entity line, out LineTrackChain chain) => m_Query.TryChain(line, out chain);
        internal bool TryProfile(Entity line, out LineTraversalProfile profile) => m_Query.TryProfile(line, out profile);
        internal bool TryInterval(Entity line, int intervalIndex, out BypassProtectedInterval interval) => m_Query.TryInterval(line, intervalIndex, out interval);
        internal bool TryScene(Entity line, int waypointIndex, out LocalBypassWaypointSceneBinding scene) => m_Query.TryScene(line, waypointIndex, out scene);
        internal bool TryGetWaypointIndexLookup(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup) => m_Query.TryGetWaypointIndexLookup(line, waypoints, out lookup);
        internal bool TryGetWaypointIndexLookup(Entity line, out LineWaypointIndexLookup lookup) => m_Query.TryGetWaypointIndexLookup(line, out lookup);

        internal void EnsureSharedTrackIndexCurrent() => m_Shared.EnsureSharedTrackIndexCurrent();
        internal void RefreshSharedRuns(LineTrackChain chain) => m_Shared.RefreshSharedRuns(chain);
        internal void EnsureBypassPipelineReady(LineTrackChain chain) => m_Intervals.EnsureBypassPipelineReady(chain);
        internal void EnsureBypassPipelineReady(LineTrackChain chain, ModeScope scope) => m_Intervals.EnsureBypassPipelineReady(chain, scope);
        internal void ResetBypassPipeline(LineTrackChain chain) => TrackIntervals.ResetBypassPipeline(chain);
        internal bool TryResolveBypassProtectedInterval(LineTrackChain chain, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out int protectedIntervalIndex, out BypassProtectedInterval protectedInterval) => m_Intervals.TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out protectedInterval);
        internal bool TryGetLocalSceneSnapshot(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out LineTrackChain chain, out LocalBypassSceneStaticSnapshot snapshot) => m_Intervals.TryGetLocalSceneSnapshot(line, waypoints, currentWaypointIndex, out chain, out snapshot);
        internal bool TryGetLocalScene(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out LineTrackChain chain, out SceneDefinition scene) => m_Intervals.TryGetLocalScene(line, waypoints, currentWaypointIndex, out chain, out scene);
        internal bool TryGetStationExitCoordinate(LineTrackChain localChain, BypassProtectedInterval localProtectedInterval, Entity currentBypassBuilding, out float stationExitCoordinate) => m_Intervals.TryGetStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out stationExitCoordinate);
        internal bool TryGetStationExitAtom(LineTrackChain localChain, BypassProtectedInterval localProtectedInterval, Entity currentBypassBuilding, out int stationExitAtomIndex) => m_Intervals.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out stationExitAtomIndex);
        internal int CountProtectedSharedIntervals(LineTrackChain chain, int protectedIntervalIndex, out bool hasMirroredContext) => m_Intervals.CountProtectedSharedIntervals(chain, protectedIntervalIndex, out hasMirroredContext);
        internal float EstimateFramesBetweenAtoms(LineTrackChain chain, int startControlEdgeIndex, int endControlEdgeIndexInclusive, int fromAtomIndex, int toAtomIndexExclusive) => m_Intervals.EstimateFramesBetweenAtoms(chain, startControlEdgeIndex, endControlEdgeIndexInclusive, fromAtomIndex, toAtomIndexExclusive);

        internal int CountIntervalPhysicalOverlap(LineTrackChain sourceChain, BypassProtectedInterval sourceInterval, LineTrackChain candidateChain, BypassProtectedInterval candidateInterval) => m_Shared.CountIntervalPhysicalOverlap(sourceChain, sourceInterval, candidateChain, candidateInterval);
        internal int ComputeIntervalOrderedRun(LineTrackChain sourceChain, BypassProtectedInterval sourceInterval, LineTrackChain candidateChain, BypassProtectedInterval candidateInterval) => m_Shared.ComputeIntervalOrderedRun(sourceChain, sourceInterval, candidateChain, candidateInterval);
        internal bool TryFindOrderedRunSpan(LineTrackChain sourceChain, BypassProtectedInterval sourceInterval, LineTrackChain candidateChain, BypassProtectedInterval candidateInterval, out int sourceStartAtomIndex, out int sourceEndAtomIndexExclusive, out int candidateStartAtomIndex, out int candidateEndAtomIndexExclusive, out int orderedRunLength) => m_Shared.TryFindOrderedRunSpan(sourceChain, sourceInterval, candidateChain, candidateInterval, out sourceStartAtomIndex, out sourceEndAtomIndexExclusive, out candidateStartAtomIndex, out candidateEndAtomIndexExclusive, out orderedRunLength);
        internal ProtectedIntervalMatch FindBestMatchingProtectedInterval(LineTrackChain sourceChain, BypassProtectedInterval sourceInterval, LineTrackChain candidateChain) => m_Shared.FindBestMatchingProtectedInterval(sourceChain, sourceInterval, candidateChain);

        internal Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> GlobalSharedTrunkSnapshots => m_Scene.GlobalSharedTrunkSnapshots;
        internal Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> ProtectedIntervalPairMetricsSnapshots => m_Scene.ProtectedIntervalPairMetricsSnapshots;
        internal void ClearAllStaticCaches()
        {
            m_Scene.ClearAll();
            m_Profile.ClearAll();
        }

        internal void ClearStaticCachesForLine(Entity line)
        {
            m_Scene.ClearLine(line);
            m_Profile.ClearLine(line);
        }

        public string BuildDevSightTooltipSummary(Entity laneEntity) => m_Diag.BuildDevSightTooltipSummary(laneEntity);
        internal void LogLineTrackChainDiagnostics(Entity line) => m_Diag.LogLineTrackChainDiagnostics(line);
        public void DumpTrackModelSnapshot() => m_Dump.DumpTrackModelSnapshot();

        internal static float EstimateAverageControlEdgeFramesPerAtom(LineTrackChain chain) => TrackIntervals.EstimateAverageControlEdgeFramesPerAtom(chain);
        internal static string FormatProtectedIntervalSummary(ProtectedIntervalSummary summary, BypassProtectedInterval interval) => TrackIntervals.FormatProtectedIntervalSummary(summary, interval);
        internal static string ClassifyProtectedIntervalTrackModelRisk(ProtectedIntervalSummary summary) => TrackIntervals.ClassifyProtectedIntervalTrackModelRisk(summary);
    }
}
