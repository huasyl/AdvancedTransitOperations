using System.Collections.Generic;
using System.Linq;
using Game.Routes;
using RapidTransitMod.Bypass;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackModelService
    {
        private const int MaxPublishedChanges = 4096;
        private readonly TrackSupport m_Support;
        private readonly TrackState m_State;
        private readonly SceneCache m_Scene;
        private readonly TrackBuild m_Build;
        private readonly TrackProfile m_Profile;
        private readonly TramStopIndex m_TramStops;
        private readonly TrackIntervals m_Intervals;
        private readonly SharedIndex m_Shared;
        private readonly TrackQuery m_Query;
        private readonly TrackDiag m_Diag;
        private readonly TrackDump m_Dump;
        private readonly Dictionary<Entity, PublishedTraversalSnapshot> m_PublishedTraversals =
            new Dictionary<Entity, PublishedTraversalSnapshot>();
        private readonly List<Entity> m_PublishedTraversalOrder = new List<Entity>();
        private readonly List<PublishedTraversalSnapshot> m_PublishedChanges =
            new List<PublishedTraversalSnapshot>();
        private ulong m_PublishedTraversalVersion;

        internal TrackModelService(ITrackModelRuntimeContext runtime)
        {
            m_Support = new TrackSupport(runtime);
            m_State = new TrackState();
            m_Scene = new SceneCache();
            m_Diag = new TrackDiag(m_Support);
            m_TramStops = new TramStopIndex(m_Support);
            m_Profile = new TrackProfile(m_Support, m_TramStops);
            m_Build = new TrackBuild(m_State, m_Support, m_Profile, m_Diag, () => m_Shared.MarkDirty(), runtime.NotifyLineTrackChainRebuilt, PublishTraversal, InvalidateLine);
            m_Shared = new SharedIndex(m_Support, m_Build);
            m_Intervals = new TrackIntervals(m_Support, m_Scene, m_Shared, m_Build);
            m_Query = new TrackQuery(m_State, m_Shared, m_Support);
            m_Diag.Bind(m_Build, m_Shared, m_Intervals);
            m_Dump = new TrackDump(m_Support, m_Build, m_Profile, m_Shared, m_Intervals, m_Diag);
        }

        internal uint SharedIndexVersion => m_Shared.Version;
        internal void MarkSharedIndexDirty() => m_Shared.MarkDirty();
        internal void Dispose() { }

        internal ulong PublishedTraversalVersion => m_PublishedTraversalVersion;

        internal int CopyPublishedTraversalSnapshot(
            int cursor,
            int budget,
            List<PublishedTraversalSnapshot> output,
            out bool complete)
        {
            complete = false;
            if (output == null || budget <= 0)
                return 0;

            output.Clear();
            int start = math.clamp(cursor, 0, m_PublishedTraversalOrder.Count);
            int end = math.min(start + budget, m_PublishedTraversalOrder.Count);
            for (int i = start; i < end; i++)
            {
                if (m_PublishedTraversals.TryGetValue(m_PublishedTraversalOrder[i], out PublishedTraversalSnapshot snapshot))
                    output.Add(snapshot);
            }

            complete = end >= m_PublishedTraversalOrder.Count;
            return output.Count;
        }

        internal int CopyPublishedTraversalChanges(
            ulong afterVersion,
            int budget,
            List<PublishedTraversalSnapshot> output)
            => CopyPublishedTraversalChanges(afterVersion, budget, output, out _);

        internal int CopyPublishedTraversalChanges(
            ulong afterVersion,
            int budget,
            List<PublishedTraversalSnapshot> output,
            out bool historyGap)
        {
            historyGap = false;
            if (output == null || budget <= 0)
                return 0;
            output.Clear();
            if (m_PublishedTraversalVersion > afterVersion
                && (m_PublishedChanges.Count == 0
                    || m_PublishedChanges[0].PublishVersion > afterVersion + 1UL))
            {
                historyGap = true;
                return 0;
            }

            int low = 0;
            int high = m_PublishedChanges.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (m_PublishedChanges[middle].PublishVersion <= afterVersion)
                    low = middle + 1;
                else
                    high = middle;
            }
            for (int i = low; i < m_PublishedChanges.Count && output.Count < budget; i++)
                output.Add(m_PublishedChanges[i]);
            return output.Count;
        }

        internal void InvalidateLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_State.MarkDirty(line);
            m_Shared.MarkDirty();
            if (m_State.RemoveLine(line, out LineTrackChain existingChain) && existingChain != null)
                m_Diag.RemoveDevSightChain(existingChain);

            PublishUnavailable(line);

            m_Scene.ClearLine(line);
            m_Profile.ClearLine(line);
            m_TramStops.RemoveLine(line);
        }

        internal void RequestTramStopAudit() => m_TramStops.RequestAudit();

        internal void TickTramStopIndex()
        {
            m_TramStops.Tick();
            var lines = new List<Entity>();
            m_TramStops.DrainDirtyLines(2, lines);
            for (int i = 0; i < lines.Count; i++)
                m_Build.RebuildTraversal(lines[i]);
        }

        internal void InvalidateWaypointIndexLookup(Entity line)
        {
            m_State.RemoveWaypointLookup(line);
        }

        internal void InvalidateAll()
        {
            foreach (Entity line in m_PublishedTraversals.Keys.ToArray())
                PublishUnavailable(line);
            m_PublishedTraversals.Clear();
            m_PublishedTraversalOrder.Clear();
            m_PublishedChanges.Clear();
            m_State.ClearLines();
            m_Shared.Clear();
            m_Scene.ClearAll();
            m_Profile.ClearAll();
            m_TramStops.Clear();
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

        private void PublishTraversal(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null || chain == null)
                return;
            chain.TraversalSignature = TraversalSignature(chain);
            PublishedTraversalSnapshot snapshot = new PublishedTraversalSnapshot
            {
                Line = line,
                PublishVersion = ++m_PublishedTraversalVersion,
                ChainSignature = chain.Signature,
                TraversalSignature = chain.TraversalSignature,
                ChainComplete = chain.ChainComplete,
                HasPhysicalTurnback = chain.TurnbackBoundaries.Count > 0,
                Available = true,
                Events = chain.TraversalProfile?.Events?.ToArray() ?? System.Array.Empty<TraversalEvent>(),
                RunChartTurnbackRegions = chain.RunChartTurnbackRegions?.ToArray()
                    ?? System.Array.Empty<RunChartTurnbackRegion>()
            };
            if (!m_PublishedTraversals.ContainsKey(line))
            {
                m_PublishedTraversalOrder.Add(line);
                SortPublishedTraversalOrder();
            }
            m_PublishedTraversals[line] = snapshot;
            AddPublishedChange(snapshot);
        }

        private void PublishUnavailable(Entity line)
        {
            if (line == Entity.Null || !m_PublishedTraversals.ContainsKey(line))
                return;
            PublishedTraversalSnapshot snapshot = new PublishedTraversalSnapshot
            {
                Line = line,
                PublishVersion = ++m_PublishedTraversalVersion,
                Available = false
            };
            m_PublishedTraversals.Remove(line);
            m_PublishedTraversalOrder.Remove(line);
            AddPublishedChange(snapshot);
        }

        private void SortPublishedTraversalOrder()
        {
            m_PublishedTraversalOrder.Sort((left, right) =>
            {
                int result = left.Index.CompareTo(right.Index);
                return result != 0 ? result : left.Version.CompareTo(right.Version);
            });
        }

        private void AddPublishedChange(PublishedTraversalSnapshot snapshot)
        {
            m_PublishedChanges.Add(snapshot);
            if (m_PublishedChanges.Count > MaxPublishedChanges)
                m_PublishedChanges.RemoveAt(0);
        }

        private ulong TraversalSignature(LineTrackChain chain)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (int)TransportModeResolver.Resolve(m_Support.EntityManager, chain.LineEntity));
            hash = Mix(hash, chain.Signature);
            hash = Mix(hash, chain.ChainComplete ? 1 : 0);
            hash = Mix(hash, chain.TurnbackBoundaries.Count > 0 ? 1 : 0);
            for (int i = 0; i < chain.TrackAtoms.Count; i++)
            {
                TrackAtom atom = chain.TrackAtoms[i];
                hash = Mix(hash, atom.Key.PhysicalLaneKey.Index);
                hash = Mix(hash, atom.TargetDelta.x);
                hash = Mix(hash, atom.TargetDelta.y);
            }
            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent item = chain.TraversalProfile.Events[i];
                hash = Mix(hash, (int)item.Kind);
                hash = Mix(hash, item.Building.Index);
                hash = Mix(hash, item.WaypointIndex);
                hash = Mix(hash, item.PassIndex);
                hash = Mix(hash, item.StartAtomIndex);
                hash = Mix(hash, item.EndAtomIndexExclusive);
                string stationId = item.StationId ?? string.Empty;
                for (int characterIndex = 0; characterIndex < stationId.Length; characterIndex++)
                    hash = Mix(hash, stationId[characterIndex]);
            }
            for (int i = 0; i < chain.RunChartTurnbackRegions.Count; i++)
            {
                RunChartTurnbackRegion region = chain.RunChartTurnbackRegions[i];
                hash = Mix(hash, region.BoundaryAtomIndex);
                hash = Mix(hash, region.StartAtomIndex);
                hash = Mix(hash, region.EndAtomIndexExclusive);
            }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                return (hash ^ value) * 1099511628211UL;
            }
        }

        private static ulong Mix(ulong hash, int value)
        {
            return Mix(hash, unchecked((ulong)(uint)value));
        }

        private static ulong Mix(ulong hash, float value)
        {
            return Mix(hash, unchecked((ulong)(uint)math.asint(value)));
        }

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
