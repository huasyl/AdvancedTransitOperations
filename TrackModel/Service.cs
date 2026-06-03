using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.UI.InGame;
using Game.Vehicles;
using Colossal.Mathematics;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed partial class TrackModelService
    {
        private const int MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS = 3;
        private const int MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN = 2;
        private const float PROTECTED_INTERVAL_TAIL_CLEARANCE_ATOMS = 1.25f;
        private const float SAME_DIRECTION_AHEAD_MARGIN_ATOMS = 0.75f;
        private const float TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES = 1f;
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;
        private const float LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS = 8f;
        private const int MAX_CONFLICT_CORRIDOR_GAP_ATOMS = 6;

        private readonly ITrackModelRuntimeContext m_Runtime;
        private readonly TrackModelStore m_Store;
        private readonly TrackModelBuilder m_Builder;
        private readonly TrackModelQuery m_Query;
        private readonly TrackModelCoordinator m_Coordinator;

        internal TrackModelService(ITrackModelRuntimeContext runtime)
        {
            m_Runtime = runtime;
            m_Store = new TrackModelStore();
            m_Builder = new TrackModelBuilder();
            m_Query = new TrackModelQuery(m_Store, m_Builder);
            m_Coordinator = new TrackModelCoordinator(m_Store, m_Builder);
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.Log;

        internal uint SharedIndexVersion => m_Builder.Version();
        internal void MarkSharedIndexDirty() => m_Builder.MarkDirty();
        internal void Dispose() { }
        internal bool TryGetChainForLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain) => TryGetLineTrackChain(line, waypoints, out chain);
        internal bool TryChain(Entity line, out LineTrackChain chain) => m_Query.TryChain(line, out chain);
        internal bool TryProfile(Entity line, out LineTraversalProfile profile) => m_Query.TryProfile(line, out profile);
        internal bool TryInterval(Entity line, int intervalIndex, out BypassProtectedInterval interval) => m_Query.TryInterval(line, intervalIndex, out interval);
        internal bool TryScene(Entity line, int waypointIndex, out LocalBypassWaypointSceneBinding scene) => m_Query.TryScene(line, waypointIndex, out scene);
        internal bool TryGetWaypointIndexLookup(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup)
        {
            lookup = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            ulong signature = ComputeLineWaypointSignature(waypoints);
            if (!m_LineWaypointIndexLookups.TryGetValue(line, out lookup)
                || lookup == null
                || lookup.Signature != signature)
            {
                lookup = new LineWaypointIndexLookup
                {
                    Signature = signature
                };

                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                    if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                        continue;

                    lookup.WaypointIndexByWaypoint[waypoint] = waypointIndex;
                    if (EntityManager.HasComponent<Connected>(waypoint))
                    {
                        Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                        if (stop != Entity.Null)
                            lookup.WaypointIndexByStop[stop] = waypointIndex;
                    }
                }

                m_LineWaypointIndexLookups[line] = lookup;
            }

            return true;
        }

        internal Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> GlobalSharedTrunkSnapshots => m_GlobalSharedTrunkSnapshots;
        internal Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> ProtectedIntervalPairMetricsSnapshots => m_ProtectedIntervalPairMetricsSnapshots;
        internal void ClearAllStaticCaches() => InvalidateAllStaticCaches();
        internal void ClearStaticCachesForLine(Entity line) => InvalidateStaticCachesForLine(line);

        private bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => m_Runtime.TryGetLineTimeProfile(line, waypoints, out profile);
        private float GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, Game.Prefabs.TransportLineData prefabLineData) => m_Runtime.GetProfileWaypointStopFrames(line, waypoints, waypointIndex, prefabLineData);
        private float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints) => m_Runtime.GetLineLoopFramesEstimate(line, waypoints);
        private float ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex) => m_Runtime.ComputeDepartureToWaypointFramesFromProfile(profile, fromWaypointIndex, targetWaypointIndex);
        private Entity Stop(Entity waypoint) => m_Runtime.Stop(waypoint);
        private Entity StationOf(Entity stop) => m_Runtime.StationOf(stop);
        private Entity ResolvePassingStationBuilding(Entity entity) => m_Runtime.ResolvePassingStation(entity);
        private bool IsAppliedLocal(Entity line) => m_Runtime.IsAppliedLocal(line);
        private bool IsAppliedExpress(Entity line) => m_Runtime.IsAppliedExpress(line);
        private static bool IsLineOrderedRuntimeLoggingEnabled() => false;
        private static float GetProtectedIntervalDisplayLength(BypassProtectedInterval interval) => math.max(1f, interval.EndAtomIndexExclusive - interval.StartAtomIndex);
        private static float MapControlPointToProtectedIntervalCoordinate(LineTrackChain chain, BypassProtectedInterval interval, int controlPointIndex)
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
        private static int ResolveControlEdgeIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                if (atomIndex >= edge.StartAtomIndex && atomIndex < edge.EndAtomIndexExclusive)
                    return i;
            }

            return -1;
        }
        private bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => m_Runtime.TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        private Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Runtime.GetBypassBuildingForWaypoint(waypoints, waypointIndex);
        private Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Runtime.GetStationBuildingForWaypoint(waypoints, waypointIndex);
        private static int CountForwardWaypointSteps(int waypointCount, int startIndexInclusive, int targetIndexInclusive)
        {
            if (waypointCount <= 0
                || startIndexInclusive < 0
                || startIndexInclusive >= waypointCount
                || targetIndexInclusive < 0
                || targetIndexInclusive >= waypointCount)
            {
                return -1;
            }

            return (targetIndexInclusive - startIndexInclusive + waypointCount) % waypointCount;
        }
        private bool TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex) => m_Runtime.TryFindWaypointIndexForBypassBuilding(waypoints, building, startIndexInclusive, out waypointIndex);
        private bool TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex) => m_Runtime.TryFindFutureSharedCorridorWaypoint(expressWaypoints, localCorridorWaypoints, startIndexInclusive, endIndexInclusive, out expressWaypointIndex, out localWaypointIndex);
        private Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding) => m_Runtime.BuildLocalBypassCorridorWaypointMap(waypoints, currentWaypointIndex, nextBypassWaypointIndex, currentBypassBuilding);
        private bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries) => m_Runtime.TryCollectTurnbackStationBoundaries(chain, stationBoundaries);
        private bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => m_Runtime.TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary);
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
        private IEnumerable<KeyValuePair<string, AppliedLine>> m_AppliedLines => m_Runtime.AppliedLines;
        private BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData => m_Runtime.GetBufferLookup<T>(isReadOnly);

        private static ulong MixLineSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }

        private static ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> wps)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, wps.Length);
            for (int i = 0; i < wps.Length; i++)
                hash = MixLineSignature(hash, wps[i].m_Waypoint.Index);
            return hash;
        }

        public string BuildDevSightLaneTooltipSummary(Entity laneEntity)
        {
            if (laneEntity == Entity.Null)
                return "target  null";

            bool hasIndex = m_DevSightLaneIndex.TryGetValue(laneEntity, out List<DevSightLaneOccurrence> occurrences);

            if (!hasIndex)
            {
                foreach (var kvp in m_DevSightLaneIndex)
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
            {
                return "target  " + FormatEntityRef(laneEntity) + "\ntrack model  no-chain-hit";
            }
            StringBuilder result = new StringBuilder(256);
            result.Append("target  ").Append(FormatEntityRef(laneEntity));

            for (int i = 0; i < occurrences.Count; i++)
            {
                result.Append('\n').Append(FormatDevSightOccurrence(occurrences[i]));
            }

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

        private static ulong ComputeProtectedIntervalAtomSignature(LineTrackChain chain, BypassProtectedInterval interval)
        {
            ulong hash = 1469598103934665603UL;
            int atomCount = 0;

            for (int atomIndex = interval.StartAtomIndex; atomIndex < interval.EndAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;

                atomCount++;
                hash = MixLineSignature(hash, atom.Key.PhysicalLaneKey.Index);
                hash = MixLineSignature(hash, atom.Key.PreviousTarget.Index);
                hash = MixLineSignature(hash, atom.Key.NextTarget.Index);
            }

            hash = MixLineSignature(hash, atomCount);
            return hash;
        }

        private bool TryGetSharedAtomContext(Entity line, TrackAtomKey key, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;
            if (!m_Query.TryTrack(key, out List<SharedTrackOccurrence> occurrences)
                || occurrences == null
                || occurrences.Count == 0)
            {
                return false;
            }

            var sharedLines = new HashSet<Entity>();
            foreach (SharedTrackOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity != line)
                    sharedLines.Add(occurrence.LineEntity);
            }

            sharedLineCount = sharedLines.Count;
            if (sharedLineCount == 0)
                return false;

            TrackAtomKey mirroredKey = new TrackAtomKey(key.PhysicalLaneKey, key.NextTarget, key.PreviousTarget);
            if (m_Query.TryTrack(mirroredKey, out List<SharedTrackOccurrence> mirroredOccurrences)
                && mirroredOccurrences != null)
            {
                foreach (SharedTrackOccurrence occurrence in mirroredOccurrences)
                {
                    if (occurrence.LineEntity != line)
                    {
                        mirroredContext = true;
                        break;
                    }
                }
            }

            return true;
        }

        private bool TryGetSharedPhysicalContext(Entity line, TrackAtom atom, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;

            if (!m_Query.TryPhysical(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                || occurrences == null
                || occurrences.Count == 0)
            {
                return false;
            }

            var sharedLines = new HashSet<Entity>();
            foreach (SharedPhysicalOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity == line)
                    continue;

                sharedLines.Add(occurrence.LineEntity);
                if (occurrence.PreviousTarget == atom.Key.NextTarget
                    && occurrence.NextTarget == atom.Key.PreviousTarget)
                {
                    mirroredContext = true;
                }
            }

            sharedLineCount = sharedLines.Count;
            return sharedLineCount > 0;
        }

        private bool TryGetSharedPhysicalContextForLine(Entity line, TrackAtom atom, Entity otherLine, out bool mirroredContext)
        {
            mirroredContext = false;
            if (otherLine == Entity.Null
                || !m_Query.TryPhysical(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                || occurrences == null)
            {
                return false;
            }

            bool found = false;
            foreach (SharedPhysicalOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity != otherLine)
                    continue;

                found = true;
                if (occurrence.PreviousTarget == atom.Key.NextTarget
                    && occurrence.NextTarget == atom.Key.PreviousTarget)
                {
                    mirroredContext = true;
                }
            }

            return found;
        }

    }
}
