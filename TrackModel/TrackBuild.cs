using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using Unity.Entities;
using Unity.Mathematics;
namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackBuild
    {
        private readonly TrackState m_State;
        private readonly TrackSupport m_Support;
        private readonly TrackProfile m_Profile;
        private readonly TrackDiag m_Diag;
        private readonly Action m_MarkSharedDirty;
        private readonly Action<Entity, LineTrackChain> m_NotifyLineTrackChainCandidate;
        private readonly Action<Entity, LineTrackChain> m_NotifyLineTrackChainEstablished;
        private readonly Action<Entity, LineTrackChain> m_PublishTraversal;
        private readonly Action<Entity> m_InvalidateLine;
        private readonly Action<Entity> m_ClearStaticCachesForLine;
        private readonly struct TrackStationBoundaryInput
        {
            internal readonly TrackAtom Incoming;
            internal readonly TrackAtom Outgoing;
            internal readonly bool IsValid;
            internal readonly bool HasTurnback;

            internal TrackStationBoundaryInput(
                TrackAtom incoming,
                TrackAtom outgoing,
                bool isValid,
                bool hasTurnback)
            {
                Incoming = incoming;
                Outgoing = outgoing;
                IsValid = isValid;
                HasTurnback = hasTurnback;
            }
        }
        private sealed class TrackBuildInputs
        {
            internal ComponentLookup<Lane> Lanes;
            internal ComponentLookup<EdgeLane> EdgeLanes;
            internal ComponentLookup<Owner> Owners;
            internal ComponentLookup<Game.Net.Edge> Edges;
            internal BufferLookup<ConnectedEdge> ConnectedEdges;
            internal BufferLookup<SubLane> SubLanes;

            internal TrackBuildInputs(TrackSupport support)
            {
                EntityManager entityManager = support.EntityManager;
                entityManager.CompleteDependencyBeforeRO<Lane>();
                entityManager.CompleteDependencyBeforeRO<EdgeLane>();
                entityManager.CompleteDependencyBeforeRO<Owner>();
                entityManager.CompleteDependencyBeforeRO<Game.Net.Edge>();
                entityManager.CompleteDependencyBeforeRO<ConnectedEdge>();
                entityManager.CompleteDependencyBeforeRO<SubLane>();
                Lanes = support.GetComponentLookup<Lane>(true);
                EdgeLanes = support.GetComponentLookup<EdgeLane>(true);
                Owners = support.GetComponentLookup<Owner>(true);
                Edges = support.GetComponentLookup<Game.Net.Edge>(true);
                ConnectedEdges = support.GetBufferLookup<ConnectedEdge>(true);
                SubLanes = support.GetBufferLookup<SubLane>(true);
            }
        }
        private readonly struct TrackChainScan
        {
            internal readonly List<TrackAtom> Atoms;
            internal readonly List<TrackSegmentRange> Ranges;
            internal readonly TrackWaypointInputBaseline[] WaypointInputs;
            internal readonly TrackSegmentInputBaseline[] SegmentInputs;
            internal readonly List<TrackExtensionRange> ExtensionRanges;
            internal readonly string ExtensionBuildNote;
            internal readonly bool ChainComplete;
            internal readonly bool HasStationTrackExtensions;
            internal bool HasData => Atoms != null
                && Ranges != null
                && WaypointInputs != null
                && SegmentInputs != null;
            internal TrackChainScan(
                List<TrackAtom> atoms,
                List<TrackSegmentRange> ranges,
                TrackWaypointInputBaseline[] waypointInputs,
                TrackSegmentInputBaseline[] segmentInputs,
                List<TrackExtensionRange> extensionRanges,
                string extensionBuildNote,
                bool chainComplete,
                bool hasStationTrackExtensions)
            {
                Atoms = atoms;
                Ranges = ranges;
                WaypointInputs = waypointInputs;
                SegmentInputs = segmentInputs;
                ExtensionRanges = extensionRanges;
                ExtensionBuildNote = extensionBuildNote ?? string.Empty;
                ChainComplete = chainComplete;
                HasStationTrackExtensions = hasStationTrackExtensions;
            }
        }
        internal TrackBuild(
            TrackState state,
            TrackSupport support,
            TrackProfile profile,
            TrackDiag diag,
            Action markSharedDirty,
            Action<Entity, LineTrackChain> notifyLineTrackChainCandidate,
            Action<Entity, LineTrackChain> notifyLineTrackChainEstablished,
            Action<Entity, LineTrackChain> publishTraversal,
            Action<Entity> invalidateLine,
            Action<Entity> clearStaticCachesForLine)
        {
            m_State = state;
            m_Support = support;
            m_Profile = profile;
            m_Diag = diag;
            m_MarkSharedDirty = markSharedDirty;
            m_NotifyLineTrackChainCandidate = notifyLineTrackChainCandidate;
            m_NotifyLineTrackChainEstablished = notifyLineTrackChainEstablished;
            m_PublishTraversal = publishTraversal;
            m_InvalidateLine = invalidateLine;
            m_ClearStaticCachesForLine = clearStaticCachesForLine;
        }
        internal EntityManager EntityManager => m_Support.EntityManager;
        internal static ulong MixLineTrackChainSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }
        private bool TryScanDirtyChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            LineTrackChain previousChain,
            out ulong signature,
            out List<TrackAtom> refreshedAtoms,
            out List<TrackSegmentRange> refreshedRanges,
            out TrackWaypointInputBaseline[] refreshedWaypointInputs,
            out TrackSegmentInputBaseline[] refreshedSegmentInputs,
            out List<TrackExtensionRange> refreshedExtensionRanges,
            out string extensionBuildNote,
            out bool scanComplete,
            out bool scanUnchanged,
            out bool hasStationTrackExtensions)
        {
            ulong hash = 1469598103934665603UL;
            TransitMode mode = TransportModeResolver.Resolve(EntityManager, line);
            hash = MixLineTrackChainSignature(hash, line.Index);
            hash = MixLineTrackChainSignature(hash, (int)mode);
            hash = MixLineTrackChainSignature(hash, waypoints.Length);
            hash = MixLineTrackChainSignature(hash, segments.Length);
            bool chainComplete = segments.Length > 0 && segments.Length == waypoints.Length;
            bool canCompare = previousChain != null
                && previousChain.Mode == mode
                && previousChain.WaypointInputs != null
                && previousChain.WaypointInputs.Length == waypoints.Length
                && previousChain.SegmentInputs != null
                && previousChain.SegmentInputs.Length == segments.Length;
            bool structureEqual = canCompare;
            refreshedAtoms = new List<TrackAtom>(previousChain != null && previousChain.TrackAtoms != null
                ? previousChain.TrackAtoms.Count
                : 0);
            refreshedRanges = new List<TrackSegmentRange>(segments.Length);
            refreshedWaypointInputs = new TrackWaypointInputBaseline[waypoints.Length];
            refreshedSegmentInputs = new TrackSegmentInputBaseline[segments.Length];
            refreshedExtensionRanges = new List<TrackExtensionRange>();
            extensionBuildNote = string.Empty;
            List<string> extensionNotes = RtLog.CacheInvalidationDiagnosticsEnabled
                ? new List<string>(3)
                : null;
            scanComplete = false;
            scanUnchanged = false;
            hasStationTrackExtensions = false;
            TrackTargetComparer targetComparer = canCompare
                ? new TrackTargetComparer(EntityManager)
                : null;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                hash = MixLineTrackChainSignature(hash, waypoint.Index);
                if (EntityManager.HasComponent<RouteLane>(waypoint))
                {
                    RouteLane routeLane = EntityManager.GetComponentData<RouteLane>(waypoint);
                    hash = MixLineTrackChainSignature(hash, routeLane.m_StartLane.Index);
                    hash = MixLineTrackChainSignature(hash, routeLane.m_EndLane.Index);
                    hash = MixLineTrackChainSignature(hash, (int)math.round(routeLane.m_StartCurvePos * 1000f));
                    hash = MixLineTrackChainSignature(hash, (int)math.round(routeLane.m_EndCurvePos * 1000f));
                }
                refreshedWaypointInputs[i] = new TrackWaypointInputBaseline(waypoint, EntityManager);
                if (canCompare)
                {
                    if (!previousChain.WaypointInputs[i].EqualsCurrent(waypoint, EntityManager))
                        structureEqual = false;
                }
            }
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                int startAtomIndex = refreshedAtoms.Count;
                Entity segmentEntity = segments[segmentIndex].m_Segment;
                hash = MixLineTrackChainSignature(hash, segmentEntity.Index);
                hash = MixLineTrackChainSignature(hash, segmentEntity.Version);
                bool segmentComplete = TryGetSegmentPathElements(segmentEntity, out DynamicBuffer<PathElement> pathElements);
                bool pathComplete = segmentComplete;
                bool hasTrackAtom = false;
                TrackSegmentInputBaseline previousSegment = default;
                bool segmentComparable = false;
                bool segmentUnchanged = true;
                TrackPathElementBaseline[] currentElementInputs = segmentComplete
                    ? new TrackPathElementBaseline[pathElements.Length]
                    : Array.Empty<TrackPathElementBaseline>();
                if (canCompare)
                {
                    previousSegment = previousChain.SegmentInputs[segmentIndex];
                    segmentComparable = previousSegment.Segment == segmentEntity
                        && previousSegment.PathComplete
                        && segmentComplete;
                    if (!segmentComparable)
                        segmentUnchanged = false;
                }
                else
                {
                    segmentUnchanged = false;
                }
                if (segmentComplete)
                {
                    hash = MixLineTrackChainSignature(hash, pathElements.Length);
                    for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
                    {
                        PathElement pathElement = pathElements[pathIndex];
                        hash = MixLineTrackChainSignature(hash, pathElement.m_Target.Index);
                        hash = MixLineTrackChainSignature(hash, pathElement.m_Target.Version);
                        hash = MixLineTrackChainSignature(hash, math.asint(pathElement.m_TargetDelta.x));
                        hash = MixLineTrackChainSignature(hash, math.asint(pathElement.m_TargetDelta.y));
                        hash = MixLineTrackChainSignature(hash, (int)pathElement.m_Flags);
                        bool classified = TryClassifyTrackAtom(pathElements, pathIndex, out TrackAtom atom);
                        bool contributes = classified
                            && atom.AtomClass != TrackAtomClass.FilteredNoise
                            && pathElement.m_Target != Entity.Null;
                        TrackAtomClass atomClass = classified ? atom.AtomClass : TrackAtomClass.Unknown;
                        TrackTraversalDir traversalDir = classified ? atom.TraversalDir : TrackTraversalDir.Unknown;
                        if (!classified || atom.AtomClass == TrackAtomClass.FilteredNoise)
                        {
                            hash = MixLineTrackChainSignature(hash, 0);
                        }
                        else
                        {
                            hash = MixLineTrackChainSignature(hash, 1);
                            hash = MixLineTrackChainSignature(hash, (int)atom.AtomClass);
                            hash = MixLineTrackChainSignature(hash, (int)atom.TraversalDir);
                            if (TryResolveJunction(atom.SourceTarget, out Entity intersection))
                            {
                                hash = MixLineTrackChainSignature(hash, 1);
                                hash = MixLineTrackChainSignature(hash, intersection.Index);
                                hash = MixLineTrackChainSignature(hash, intersection.Version);
                            }
                            else
                            {
                                hash = MixLineTrackChainSignature(hash, 0);
                            }
                            hasTrackAtom = true;
                            refreshedAtoms.Add(atom);
                        }
                        bool elementStable = segmentComparable
                            && pathIndex < previousSegment.Elements.Length
                            && previousSegment.Elements[pathIndex].StableEquals(
                                targetComparer,
                                pathElement,
                                atomClass,
                                traversalDir,
                                contributes);
                        if (!elementStable)
                            segmentUnchanged = false;
                        currentElementInputs[pathIndex] = new TrackPathElementBaseline(
                            pathElement,
                            atomClass,
                            traversalDir,
                            contributes);
                    }
                }
                segmentComplete &= hasTrackAtom;
                hash = MixLineTrackChainSignature(hash, segmentComplete ? 1 : 0);
                chainComplete &= segmentComplete;
                if (canCompare && previousSegment.Elements.Length != currentElementInputs.Length)
                    segmentUnchanged = false;
                if (canCompare && !segmentUnchanged)
                    structureEqual = false;
                refreshedRanges.Add(new TrackSegmentRange(
                    startAtomIndex,
                    refreshedAtoms.Count));
                refreshedSegmentInputs[segmentIndex] = new TrackSegmentInputBaseline(
                    segmentEntity,
                    pathComplete,
                    currentElementInputs);
            }
            if (chainComplete
                && TransportModeProfile.GetProfile(mode).Lifecycle == LifecycleKind.Rail)
            {
                TrackBuildInputs buildInputs = mode == TransitMode.Tram
                    ? null
                    : new TrackBuildInputs(m_Support);
                TrackStationBoundaryInput[] stationBoundaries = mode == TransitMode.Tram
                    ? Array.Empty<TrackStationBoundaryInput>()
                    : BuildStationBoundaries(refreshedSegmentInputs, refreshedAtoms, refreshedRanges);
                bool[] pathTurnbacks = mode == TransitMode.Tram
                    ? Array.Empty<bool>()
                    : new bool[refreshedSegmentInputs.Length];
                int rawAtomCount = refreshedAtoms.Count;
                bool pathTurnbackExtensions = mode != TransitMode.Tram
                    && AppendPathTurnbacks(
                    mode,
                    refreshedSegmentInputs,
                    refreshedAtoms,
                    refreshedRanges,
                    refreshedExtensionRanges,
                    extensionNotes,
                    buildInputs,
                    pathTurnbacks);
                bool stationExtensions = AppendStationTrackAtoms(
                    mode,
                    waypoints,
                    stationBoundaries,
                    refreshedAtoms,
                    refreshedRanges,
                    refreshedExtensionRanges,
                    extensionNotes,
                    buildInputs,
                    pathTurnbacks);
                hasStationTrackExtensions = pathTurnbackExtensions || stationExtensions;
                if (hasStationTrackExtensions)
                {
                    hash = MixLineTrackChainSignature(hash, refreshedAtoms.Count - rawAtomCount);
                    for (int atomIndex = 0; atomIndex < refreshedAtoms.Count; atomIndex++)
                    {
                        TrackAtom atom = refreshedAtoms[atomIndex];
                        hash = MixLineTrackChainSignature(hash, atom.SourceTarget.Index);
                        hash = MixLineTrackChainSignature(hash, atom.SourceTarget.Version);
                        hash = MixLineTrackChainSignature(hash, atom.Key.PreviousTarget.Index);
                        hash = MixLineTrackChainSignature(hash, atom.Key.PreviousTarget.Version);
                        hash = MixLineTrackChainSignature(hash, atom.Key.NextTarget.Index);
                        hash = MixLineTrackChainSignature(hash, atom.Key.NextTarget.Version);
                        hash = MixLineTrackChainSignature(hash, math.asint(atom.TargetDelta.x));
                        hash = MixLineTrackChainSignature(hash, math.asint(atom.TargetDelta.y));
                        hash = MixLineTrackChainSignature(hash, (int)atom.SourceFlags);
                        hash = MixLineTrackChainSignature(hash, (int)atom.TraversalDir);
                        Entity stationBuilding = m_Support.ResolvePassingStationBuilding(atom.Key.PhysicalLaneKey);
                        hash = MixLineTrackChainSignature(hash, stationBuilding.Index);
                        hash = MixLineTrackChainSignature(hash, stationBuilding.Version);
                    }
                    hash = MixLineTrackChainSignature(hash, refreshedExtensionRanges.Count);
                    for (int rangeIndex = 0; rangeIndex < refreshedExtensionRanges.Count; rangeIndex++)
                    {
                        TrackExtensionRange range = refreshedExtensionRanges[rangeIndex];
                        hash = MixLineTrackChainSignature(hash, range.StartAtomIndex);
                        hash = MixLineTrackChainSignature(hash, range.ForwardEndAtomIndexExclusive);
                        hash = MixLineTrackChainSignature(hash, range.ResumeAtomIndex);
                    }
                }
            }
            if (extensionNotes != null && extensionNotes.Count > 0)
                extensionBuildNote = string.Join(";", extensionNotes);
            hash = MixLineTrackChainSignature(hash, chainComplete ? 1 : 0);
            signature = hash;
            scanComplete = chainComplete
                && refreshedAtoms.Count > 0
                && refreshedRanges.Count == segments.Length;
            // The signature omits target components; keep the scan verdict for same-hash changes.
            if (!canCompare || !structureEqual)
                return false;
            if (refreshedAtoms.Count != previousChain.TrackAtoms.Count
                || refreshedRanges.Count != previousChain.SegmentRanges.Count
                || refreshedExtensionRanges.Count != previousChain.TrackExtensionRanges.Count)
            {
                return false;
            }
            for (int i = 0; i < refreshedRanges.Count; i++)
            {
                TrackSegmentRange oldRange = previousChain.SegmentRanges[i];
                TrackSegmentRange newRange = refreshedRanges[i];
                if (oldRange.StartAtomIndex != newRange.StartAtomIndex
                    || oldRange.EndAtomIndexExclusive != newRange.EndAtomIndexExclusive)
                {
                    return false;
                }
            }
            for (int i = 0; i < refreshedExtensionRanges.Count; i++)
            {
                TrackExtensionRange oldRange = previousChain.TrackExtensionRanges[i];
                TrackExtensionRange newRange = refreshedExtensionRanges[i];
                if (oldRange.StartAtomIndex != newRange.StartAtomIndex
                    || oldRange.ForwardEndAtomIndexExclusive != newRange.ForwardEndAtomIndexExclusive
                    || oldRange.ResumeAtomIndex != newRange.ResumeAtomIndex)
                {
                    return false;
                }
            }
            for (int i = 0; i < refreshedAtoms.Count; i++)
            {
                if (!refreshedAtoms[i].Key.Equals(previousChain.TrackAtoms[i].Key)
                    || refreshedAtoms[i].SourceTarget != previousChain.TrackAtoms[i].SourceTarget)
                {
                    return true;
                }
            }
            scanUnchanged = true;
            return false;
        }
        internal bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            return TryGetChain(line, waypoints, out chain);
        }
        private bool TryGetChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null)
            {
                return false;
            }
            uint nowFrame = m_Support.FrameIndex;
            if (m_State.TryFrameSnapshot(line, out LineTrackChainFrameSnapshot frameSnapshot)
                && frameSnapshot.Frame == nowFrame
                && frameSnapshot.WaypointCount == waypoints.Length)
            {
                chain = frameSnapshot.Chain;
                if (chain != null
                    && TransportModeProfile.GetProfile(chain.Mode).Lifecycle != LifecycleKind.Rail)
                {
                    chain = null;
                    m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                        nowFrame,
                        waypoints.Length,
                        false,
                        null));
                    return false;
                }
                return frameSnapshot.Available;
            }
            m_State.TryChain(line, out LineTrackChain previousChain);
            bool trustedRailChain = previousChain != null
                && TransportModeProfile.GetProfile(previousChain.Mode).Lifecycle == LifecycleKind.Rail;
            if (previousChain != null && !trustedRailChain)
            {
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null));
                return false;
            }
            if (!EntityManager.Exists(line))
            {
                if (trustedRailChain)
                    InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            if (!trustedRailChain)
            {
                TransitMode mode = TransportModeResolver.Resolve(EntityManager, line);
                if (TransportModeProfile.GetProfile(mode).Lifecycle != LifecycleKind.Rail)
                {
                    m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                        nowFrame,
                        waypoints.Length,
                        false,
                        null));
                    return false;
                }
            }
            if (waypoints.Length == 0 || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            if (!m_State.IsDirty(line)
                && m_State.TryChain(line, out chain)
                && chain != null)
            {
                bool available = chain.ChainComplete && chain.TrackAtoms.Count > 0;
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    available,
                    available ? chain : null));
                if (!available)
                    chain = null;
                return available;
            }
            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
            {
                InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            chain = previousChain;
            ulong signature;
            bool equivalentRefresh = false;
            List<TrackAtom> refreshedAtoms = null;
            List<TrackSegmentRange> refreshedRanges = null;
            TrackWaypointInputBaseline[] refreshedWaypointInputs = null;
            TrackSegmentInputBaseline[] refreshedSegmentInputs = null;
            List<TrackExtensionRange> refreshedExtensionRanges = null;
            string extensionBuildNote = string.Empty;
            bool scanComplete = false;
            bool scanUnchanged = false;
            equivalentRefresh = TryScanDirtyChain(
                line,
                waypoints,
                segments,
                previousChain,
                out signature,
                out refreshedAtoms,
                out refreshedRanges,
                out refreshedWaypointInputs,
                out refreshedSegmentInputs,
                out refreshedExtensionRanges,
                out extensionBuildNote,
                out scanComplete,
                out scanUnchanged,
                out bool hasStationTrackExtensions);
            if (previousChain != null && scanUnchanged && previousChain.Signature == signature)
            {
                bool available = previousChain.ChainComplete && previousChain.TrackAtoms.Count > 0;
                m_State.PutChain(line, previousChain);
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    available,
                    available ? previousChain : null));
                if (!available)
                    chain = null;
                m_NotifyLineTrackChainCandidate?.Invoke(line, chain);
                return available;
            }
            ulong previousSignature = previousChain != null ? previousChain.Signature : 0UL;
            int previousAtomCount = previousChain != null ? previousChain.TrackAtoms.Count : 0;
            if (equivalentRefresh)
            {
                m_Diag.RemoveDevSightChain(previousChain);
                previousChain.TrackAtoms = refreshedAtoms;
                previousChain.SegmentRanges = refreshedRanges;
                previousChain.Signature = signature;
                previousChain.WaypointInputs = refreshedWaypointInputs;
                previousChain.SegmentInputs = refreshedSegmentInputs;
                previousChain.TrackExtensionRanges = refreshedExtensionRanges;
                previousChain.HasStationTrackExtensions = hasStationTrackExtensions;
                TrackIntervals.ResetBypassPipeline(previousChain);
                previousChain.LocalBypassWaypointScenes = Array.Empty<LocalBypassWaypointSceneBinding>();
                previousChain.LocalBypassWaypointScenesVersion = 0;
                previousChain.ControlPoints.Clear();
                previousChain.EndpointMarkers.Clear();
                previousChain.ControlEdges.Clear();
                for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
                {
                    int startAtomIndex = previousChain.SegmentRanges[waypointIndex].StartAtomIndex;
                    TryAppendControlPoint(previousChain.ControlPoints, line, waypoints, waypointIndex, startAtomIndex);
                    TryAppendEndpointMarker(previousChain.EndpointMarkers, waypoints, waypointIndex, startAtomIndex);
                }
                BuildAtomStationBuildings(previousChain);
                BuildControlEdges(previousChain, line, waypoints);
                BuildAtomIndicesByLane(previousChain);
                BuildJunctionMarkers(previousChain);
                EquivalentTrackRefresh.RefreshTraversalLaneKeys(previousChain);
                m_ClearStaticCachesForLine?.Invoke(line);
                m_Profile.RegisterTramLine(line, previousChain, waypoints);
                m_Profile.BuildTraversalProfile(previousChain, line, waypoints);
                m_Profile.BuildRunChartTurnbacks(previousChain, line);
                m_Profile.BuildTurnbackBoundaries(previousChain, line, waypoints);
                m_Profile.LogTrackModelTurnbackBuild(previousChain);
                m_State.PutChain(line, previousChain);
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    true,
                    previousChain));
                m_NotifyLineTrackChainCandidate?.Invoke(line, previousChain);
                m_PublishTraversal?.Invoke(line, previousChain);
                m_Diag.AddDevSightChain(previousChain);
                m_MarkSharedDirty?.Invoke();
                return true;
            }
            if (previousChain != null && !scanComplete)
            {
                InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            TrackChainScan scan = new TrackChainScan(
                refreshedAtoms,
                refreshedRanges,
                refreshedWaypointInputs,
                refreshedSegmentInputs,
                refreshedExtensionRanges,
                extensionBuildNote,
                scanComplete,
                hasStationTrackExtensions);
            chain = BuildLineTrackChain(line, waypoints, segments, signature, scan);
            if (chain == null || chain.TrackAtoms.Count == 0)
            {
                InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                m_Support.Log.Info("[TrackChainRebuilt] line=" + line.Index
                    + " oldSig=" + previousSignature
                    + " newSig=" + signature
                    + " waypoints=" + waypoints.Length
                    + " segments=" + segments.Length
                    + " oldAtoms=" + previousAtomCount
                    + " newAtoms=" + chain.TrackAtoms.Count
                    + " extensions=" + chain.TrackExtensionRanges.Count
                    + " extensionRanges=" + FormatExtensionRanges(chain.TrackExtensionRanges)
                    + " extensionNote=" + (string.IsNullOrEmpty(scan.ExtensionBuildNote) ? "-" : scan.ExtensionBuildNote)
                    + " frame=" + nowFrame);
            }
            if (previousChain != null)
                m_Diag.RemoveDevSightChain(previousChain);
            m_State.PutChain(line, chain);
            bool chainAvailable = chain.ChainComplete && chain.TrackAtoms.Count > 0;
            m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                nowFrame,
                waypoints.Length,
                chainAvailable,
                chainAvailable ? chain : null));
            m_NotifyLineTrackChainCandidate?.Invoke(line, chain);
            if (previousChain == null)
                m_NotifyLineTrackChainEstablished?.Invoke(line, chain);
            m_PublishTraversal?.Invoke(line, chain);
            m_Diag.AddDevSightChain(chain);
            m_MarkSharedDirty?.Invoke();
            if (!chainAvailable)
                chain = null;
            return chainAvailable;
        }
        internal void ConfirmLineChange(Entity line)
        {
            if (line == Entity.Null
                || !m_State.IsDirty(line))
            {
                return;
            }
            uint nowFrame = m_Support.FrameIndex;
            if (!EntityManager.Exists(line)
                || !EntityManager.HasComponent<TransportLine>(line)
                || !EntityManager.HasBuffer<RouteWaypoint>(line)
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                InvalidateUnavailableChain(line, nowFrame, 0);
                return;
            }
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            TryGetChain(line, waypoints, out _);
        }
        private LineTrackChain BuildLineTrackChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature,
            TrackChainScan scan)
        {
            if (!scan.HasData)
                return null;
            var chain = new LineTrackChain
            {
                LineEntity = line,
                Mode = TransportModeResolver.Resolve(EntityManager, line),
                Signature = signature,
                ChainComplete = scan.ChainComplete,
                WaypointInputs = scan.WaypointInputs,
                SegmentInputs = scan.SegmentInputs,
                TrackAtoms = scan.Atoms,
                SegmentRanges = scan.Ranges,
                TrackExtensionRanges = scan.ExtensionRanges
            };
            chain.HasStationTrackExtensions = scan.HasStationTrackExtensions;
            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int startAtomIndex = chain.SegmentRanges[waypointIndex].StartAtomIndex;
                TryAppendControlPoint(chain.ControlPoints, line, waypoints, waypointIndex, startAtomIndex);
                TryAppendEndpointMarker(chain.EndpointMarkers, waypoints, waypointIndex, startAtomIndex);
            }
            BuildAtomStationBuildings(chain);
            BuildControlEdges(chain, line, waypoints);
            BuildAtomIndicesByLane(chain);
            BuildJunctionMarkers(chain);
            m_Profile.RegisterTramLine(line, chain, waypoints);
            m_Profile.BuildTraversalProfile(chain, line, waypoints);
            m_Profile.BuildRunChartTurnbacks(chain, line);
            m_Profile.BuildTurnbackBoundaries(chain, line, waypoints);
            m_Profile.LogTrackModelTurnbackBuild(chain);
            return chain;
        }
        private bool TryGetSegmentPathElements(
            Entity segment,
            out DynamicBuffer<PathElement> pathElements)
        {
            pathElements = default;
            if (segment == Entity.Null
                || !EntityManager.Exists(segment)
                || !EntityManager.HasBuffer<PathElement>(segment)
                || !EntityManager.HasComponent<PathInformation>(segment)
                || EntityManager.GetComponentData<PathInformation>(segment).m_Distance < 0f)
            {
                return false;
            }
            pathElements = EntityManager.GetBuffer<PathElement>(segment, true);
            return pathElements.Length > 0;
        }
        private void InvalidateUnavailableChain(
            Entity line,
            uint frame,
            int waypointCount)
        {
            m_NotifyLineTrackChainCandidate?.Invoke(line, null);
            m_InvalidateLine?.Invoke(line);
            m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                frame,
                waypointCount,
                false,
                null));
        }
        internal void RebuildTraversal(Entity line)
        {
            if (line == Entity.Null
                || !m_State.TryChain(line, out LineTrackChain chain)
                || chain == null
                || !EntityManager.HasBuffer<RouteWaypoint>(line)
                || TransportModeResolver.Resolve(EntityManager, line) != TransitMode.Tram)
            {
                return;
            }
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            m_Profile.BuildTraversalProfile(chain, line, waypoints);
            m_PublishTraversal?.Invoke(line, chain);
        }
        private bool TryClassifyTrackAtom(
            DynamicBuffer<PathElement> pathElements,
            int pathIndex,
            out TrackAtom atom)
        {
            atom = default;
            if (pathIndex < 0 || pathIndex >= pathElements.Length)
                return false;
            PathElement element = pathElements[pathIndex];
            if (element.m_Target == Entity.Null)
                return false;
            TrackAtomClass atomClass = ClassifyPathElementTarget(element);
            TrackTraversalDir traversalDir = ResolveTraversalDirection(pathElements, pathIndex);
            Entity previousTarget = pathIndex > 0 ? pathElements[pathIndex - 1].m_Target : Entity.Null;
            Entity nextTarget = pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target : Entity.Null;
            TrackAtomKey key = new TrackAtomKey(element.m_Target, previousTarget, nextTarget);
            atom = new TrackAtom(key, element.m_Target, element.m_TargetDelta, element.m_Flags, atomClass, traversalDir);
            return true;
        }

        private TrackStationBoundaryInput[] BuildStationBoundaries(
            TrackSegmentInputBaseline[] segments,
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges)
        {
            var boundaries = new TrackStationBoundaryInput[segments.Length];
            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int incomingSegment = waypointIndex == 0 ? segments.Length - 1 : waypointIndex - 1;
                bool hasIncoming = TryGetIncomingSuffix(
                    segments[incomingSegment],
                    atoms,
                    ranges[incomingSegment],
                    out TrackAtom incoming);
                bool hasOutgoing = TryGetOutgoingPrefix(
                    segments[waypointIndex],
                    atoms,
                    ranges[waypointIndex],
                    out TrackAtom outgoing,
                    out _);
                boundaries[waypointIndex] = new TrackStationBoundaryInput(
                    incoming,
                    outgoing,
                    hasIncoming && hasOutgoing,
                    hasIncoming && hasOutgoing && HasWaypointTurnback(segments, waypointIndex));
            }
            return boundaries;
        }

        private static bool TryFindCurrentBoundaryAtom(
            List<TrackAtom> atoms,
            TrackSegmentRange range,
            TrackAtom source,
            out int atomIndex)
        {
            atomIndex = -1;
            for (int index = range.StartAtomIndex;
                index < range.EndAtomIndexExclusive && index < atoms.Count;
                index++)
            {
                TrackAtom candidate = atoms[index];
                if (candidate.Key.Equals(source.Key)
                    && candidate.SourceTarget == source.SourceTarget
                    && candidate.TargetDelta.Equals(source.TargetDelta)
                    && candidate.SourceFlags == source.SourceFlags
                    && candidate.AtomClass == source.AtomClass)
                {
                    atomIndex = index;
                    return true;
                }
            }
            return false;
        }

        private bool AppendStationTrackAtoms(
            TransitMode mode,
            DynamicBuffer<RouteWaypoint> waypoints,
            TrackStationBoundaryInput[] stationBoundaries,
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges,
            List<TrackExtensionRange> extensionRanges,
            List<string> extensionNotes,
            TrackBuildInputs buildInputs,
            bool[] pathTurnbacks)
        {
            if (waypoints.Length == 0 || atoms.Count == 0 || ranges.Count != waypoints.Length)
                return false;

            bool changed = false;
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                Entity building = m_Support.GetStationBuildingForWaypoint(waypoints, waypointIndex);
                if (mode == TransitMode.Tram)
                {
                    if (TryAppendTramStopTurnback(
                            waypoints,
                            atoms,
                            ranges,
                            extensionRanges,
                            waypointIndex,
                            building,
                            out bool tramTurnbackCandidate))
                    {
                        changed = true;
                    }
                    else if (tramTurnbackCandidate)
                    {
                        AppendExtensionNote(extensionNotes, "tram:wp" + waypointIndex + ":lane-or-exit-unconfirmed");
                    }
                    continue;
                }
                if (building == Entity.Null || !EntityManager.HasComponent<RouteLane>(waypoint))
                    continue;
                if (waypointIndex < pathTurnbacks.Length && pathTurnbacks[waypointIndex])
                    continue;

                if (waypointIndex >= stationBoundaries.Length)
                    continue;
                TrackStationBoundaryInput boundary = stationBoundaries[waypointIndex];
                if (!boundary.IsValid
                    || !TryFindCurrentBoundaryAtom(
                        atoms,
                        ranges[waypointIndex],
                        boundary.Outgoing,
                        out int insertIndex))
                    continue;

                Entity incomingLane = AtomLane(boundary.Incoming);
                if (m_Support.ResolvePassingStationBuilding(incomingLane) != building
                    || !TryGetAtomDirection(boundary.Incoming, out bool incomingForward)
                    || !TryGetAtomDirection(boundary.Outgoing, out bool outgoingForward))
                    continue;

                var stationPath = new List<TrackAtom>();
                bool hasStationPath = TryBuildStationPath(
                    boundary.Incoming,
                    incomingLane,
                    incomingForward,
                    building,
                    float.PositiveInfinity,
                    stationPath,
                    out bool reachedBoundary,
                    buildInputs);
                if (!hasStationPath)
                {
                    if (!reachedBoundary)
                        AppendExtensionNote(extensionNotes, "station:wp" + waypointIndex + ":path-unconfirmed");
                    continue;
                }

                var additions = new List<TrackAtom>();
                int turnbackForwardCount = -1;
                bool outgoingConnected = false;
                outgoingConnected = TryAppendStationPath(
                    stationPath,
                    boundary.Outgoing,
                    outgoingForward,
                    boundary.HasTurnback,
                    additions,
                    out turnbackForwardCount,
                    buildInputs);

                if (!outgoingConnected || additions.Count == 0)
                {
                    if (!outgoingConnected)
                        AppendExtensionNote(extensionNotes, "station:wp" + waypointIndex + ":exit-unconfirmed");
                    continue;
                }

                InsertExtension(atoms, ranges, extensionRanges, waypointIndex, insertIndex, additions, turnbackForwardCount);
                changed = true;
            }
            return changed;
        }

        private static bool IsNonZero(TrackPathElementBaseline element)
        {
            return math.asfloat(element.TargetDeltaXBits) != math.asfloat(element.TargetDeltaYBits);
        }

        private bool TryGetIncomingSuffix(
            TrackSegmentInputBaseline segment,
            List<TrackAtom> atoms,
            TrackSegmentRange range,
            out TrackAtom incoming)
        {
            incoming = default;
            int atomIndex = range.StartAtomIndex;
            for (int index = 0; index < segment.Elements.Length; index++)
            {
                TrackPathElementBaseline element = segment.Elements[index];
                int currentAtomIndex = element.HasAtomContribution ? atomIndex++ : -1;
                if (!IsNonZero(element)
                    || currentAtomIndex < 0
                    || currentAtomIndex >= atoms.Count
                    || element.AtomClass != TrackAtomClass.PrimaryLane)
                {
                    continue;
                }

                TrackAtom candidate = atoms[currentAtomIndex];
                if (TryGetAtomDirection(candidate, out _))
                {
                    incoming = candidate;
                }
            }
            return incoming.SourceTarget != Entity.Null;
        }

        private bool TryGetOutgoingPrefix(
            TrackSegmentInputBaseline segment,
            List<TrackAtom> atoms,
            TrackSegmentRange range,
            out TrackAtom outgoing,
            out int atomIndex)
        {
            outgoing = default;
            atomIndex = range.StartAtomIndex;
            for (int index = 0; index < segment.Elements.Length; index++)
            {
                TrackPathElementBaseline element = segment.Elements[index];
                int currentAtomIndex = element.HasAtomContribution ? atomIndex++ : -1;
                if (!IsNonZero(element)
                    || currentAtomIndex < 0
                    || currentAtomIndex >= atoms.Count
                    || element.AtomClass != TrackAtomClass.PrimaryLane)
                {
                    continue;
                }

                outgoing = atoms[currentAtomIndex];
                atomIndex = currentAtomIndex;
                return TryGetAtomDirection(outgoing, out _);
            }
            return false;
        }

        private bool HasWaypointTurnback(TrackSegmentInputBaseline[] segments, int waypointIndex)
        {
            int incomingSegment = waypointIndex == 0 ? segments.Length - 1 : waypointIndex - 1;
            TrackPathElementBaseline previous = default;
            bool hasPrevious = false;
            TrackPathElementBaseline[] incoming = segments[incomingSegment].Elements;
            for (int index = 0; index < incoming.Length; index++)
            {
                TrackPathElementBaseline current = incoming[index];
                if (!IsNonZero(current))
                {
                    if ((current.Flags & PathElementFlags.Return) != 0)
                        hasPrevious = false;
                    continue;
                }
                previous = current;
                hasPrevious = (current.Flags & PathElementFlags.Return) == 0;
            }

            TrackPathElementBaseline[] outgoing = segments[waypointIndex].Elements;
            for (int index = 0; index < outgoing.Length; index++)
            {
                TrackPathElementBaseline current = outgoing[index];
                if (!IsNonZero(current))
                {
                    if ((current.Flags & PathElementFlags.Return) != 0)
                        hasPrevious = false;
                    continue;
                }
                return hasPrevious && IsPathTurnbackPair(previous, current);
            }
            return false;
        }

        private bool AppendPathTurnbacks(
            TransitMode mode,
            TrackSegmentInputBaseline[] segmentInputs,
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges,
            List<TrackExtensionRange> extensionRanges,
            List<string> extensionNotes,
            TrackBuildInputs buildInputs,
            bool[] pathTurnbacks)
        {
            bool changed = false;
            int previousAtomIndex = -1;
            TrackPathElementBaseline previous = default;
            bool hasPrevious = false;
            int previousSegmentIndex = segmentInputs.Length - 1;
            if (segmentInputs.Length > 0)
            {
                TryGetTailPathContext(
                    segmentInputs[segmentInputs.Length - 1],
                    ranges[ranges.Count - 1],
                    out previous,
                    out previousAtomIndex,
                    out hasPrevious);
            }
            for (int segmentIndex = 0; segmentIndex < segmentInputs.Length; segmentIndex++)
            {
                TrackSegmentRange range = ranges[segmentIndex];
                TrackPathElementBaseline[] elements = segmentInputs[segmentIndex].Elements;
                int nextAtomIndex = range.StartAtomIndex;
                for (int pathIndex = 0; pathIndex < elements.Length; pathIndex++)
                {
                    TrackPathElementBaseline current = elements[pathIndex];
                    bool hasAtom = current.HasAtomContribution;
                    int currentAtomIndex = -1;
                    if (hasAtom)
                    {
                        if (nextAtomIndex >= range.EndAtomIndexExclusive || nextAtomIndex >= atoms.Count)
                            break;
                        currentAtomIndex = nextAtomIndex++;
                    }

                    if (math.asfloat(current.TargetDeltaXBits)
                        == math.asfloat(current.TargetDeltaYBits))
                    {
                        if ((current.Flags & PathElementFlags.Return) != 0)
                            hasPrevious = false;
                        continue;
                    }
                    bool pathTurnbackPair = hasPrevious
                        && previousAtomIndex >= 0
                        && currentAtomIndex >= 0
                        && IsPathTurnbackPair(previous, current);
                    int insertIndex = currentAtomIndex;
                    if (pathTurnbackPair
                        && TryAppendPathTurnback(
                            mode,
                            atoms,
                            ranges,
                            extensionRanges,
                            segmentIndex,
                            previousAtomIndex,
                            currentAtomIndex,
                            out int insertedCount,
                            buildInputs))
                    {
                        if (previousAtomIndex >= insertIndex)
                            previousAtomIndex += insertedCount;
                        currentAtomIndex += insertedCount;
                        nextAtomIndex += insertedCount;
                        range = ranges[segmentIndex];
                        changed = true;
                        if (previousSegmentIndex != segmentIndex)
                            pathTurnbacks[segmentIndex] = true;
                    }
                    else if (pathTurnbackPair)
                    {
                        AppendExtensionNote(extensionNotes, "path:seg" + segmentIndex + ":extension-unconfirmed");
                    }

                    previous = current;
                    previousAtomIndex = currentAtomIndex;
                    hasPrevious = (current.Flags & PathElementFlags.Return) == 0;
                    previousSegmentIndex = segmentIndex;
                }
            }
            return changed;
        }

        private static void TryGetTailPathContext(
            TrackSegmentInputBaseline segment,
            TrackSegmentRange range,
            out TrackPathElementBaseline previous,
            out int previousAtomIndex,
            out bool hasPrevious)
        {
            previous = default;
            previousAtomIndex = -1;
            hasPrevious = false;
            int atomIndex = range.StartAtomIndex;
            for (int index = 0; index < segment.Elements.Length; index++)
            {
                TrackPathElementBaseline current = segment.Elements[index];
                int currentAtomIndex = current.HasAtomContribution ? atomIndex++ : -1;
                if (!IsNonZero(current))
                {
                    if ((current.Flags & PathElementFlags.Return) != 0)
                        hasPrevious = false;
                    continue;
                }
                previous = current;
                previousAtomIndex = currentAtomIndex;
                hasPrevious = (current.Flags & PathElementFlags.Return) == 0;
            }
        }

        private bool TryAppendPathTurnback(
            TransitMode mode,
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges,
            List<TrackExtensionRange> extensionRanges,
            int segmentIndex,
            int incomingAtomIndex,
            int outgoingAtomIndex,
            out int insertedCount,
            TrackBuildInputs buildInputs)
        {
            insertedCount = 0;
            if (incomingAtomIndex < 0
                || outgoingAtomIndex >= atoms.Count)
            {
                return false;
            }

            TrackAtom incoming = atoms[incomingAtomIndex];
            TrackAtom outgoing = atoms[outgoingAtomIndex];
            Entity incomingLane = AtomLane(incoming);
            Entity building = m_Support.ResolvePassingStationBuilding(incomingLane);
            float maxDistance = building == Entity.Null
                ? GetPathTurnbackMaxDistance(mode)
                : float.PositiveInfinity;
            if (incoming.AtomClass != TrackAtomClass.PrimaryLane
                || outgoing.AtomClass != TrackAtomClass.PrimaryLane
                || maxDistance <= 0f
                || !TryGetAtomDirection(incoming, out bool incomingForward)
                || !TryGetAtomDirection(outgoing, out bool outgoingForward))
            {
                return false;
            }

            var stationPath = new List<TrackAtom>();
            if (!TryBuildStationPath(
                    incoming,
                    incomingLane,
                    incomingForward,
                    building,
                    maxDistance,
                    stationPath,
                    out _,
                    buildInputs))
            {
                return false;
            }

            var additions = new List<TrackAtom>();
            if (!TryAppendStationPath(
                    stationPath,
                    outgoing,
                    outgoingForward,
                    true,
                    additions,
                    out int forwardCount,
                    buildInputs)
                || forwardCount == 0
                || additions.Count <= forwardCount)
            {
                return false;
            }

            InsertExtension(atoms, ranges, extensionRanges, segmentIndex, outgoingAtomIndex, additions, forwardCount);
            insertedCount = additions.Count;
            return true;
        }

        private static float GetPathTurnbackMaxDistance(TransitMode mode)
        {
            if (mode == TransitMode.Train)
                return VehicleUtils.MAX_TRAIN_LENGTH;
            if (mode == TransitMode.Subway)
                return VehicleUtils.MAX_SUBWAY_LENGTH;
            return 0f;
        }

        private bool IsPathTurnbackPair(
            TrackPathElementBaseline incoming,
            TrackPathElementBaseline outgoing)
        {
            if (incoming.Target == Entity.Null
                || outgoing.Target == Entity.Null
                || (incoming.Flags & PathElementFlags.Return) != 0
                || !EntityManager.HasComponent<Owner>(incoming.Target)
                || !EntityManager.HasComponent<Owner>(outgoing.Target)
                || !EntityManager.HasComponent<Curve>(incoming.Target)
                || !EntityManager.HasComponent<Curve>(outgoing.Target))
            {
                return false;
            }

            Entity owner = EntityManager.GetComponentData<Owner>(incoming.Target).m_Owner;
            if (owner == Entity.Null
                || owner != EntityManager.GetComponentData<Owner>(outgoing.Target).m_Owner)
            {
                return false;
            }

            float2 incomingDelta = new float2(
                math.asfloat(incoming.TargetDeltaXBits),
                math.asfloat(incoming.TargetDeltaYBits));
            float2 outgoingDelta = new float2(
                math.asfloat(outgoing.TargetDeltaXBits),
                math.asfloat(outgoing.TargetDeltaYBits));
            Curve incomingCurve = EntityManager.GetComponentData<Curve>(incoming.Target);
            Curve outgoingCurve = EntityManager.GetComponentData<Curve>(outgoing.Target);
            float3 incomingTangent = MathUtils.Tangent(incomingCurve.m_Bezier, incomingDelta.y);
            float3 outgoingTangent = MathUtils.Tangent(outgoingCurve.m_Bezier, outgoingDelta.x);
            bool incomingForward = incomingDelta.y > incomingDelta.x;
            bool outgoingForward = outgoingDelta.y > outgoingDelta.x;
            float tangentDirection = incomingForward != outgoingForward ? -1f : 1f;
            return math.dot(incomingTangent, outgoingTangent) * tangentDirection < 0f;
        }

        private static void AppendExtensionNote(List<string> notes, string note)
        {
            if (notes != null && notes.Count < 3)
                notes.Add(note);
        }

        private static string FormatExtensionRanges(List<TrackExtensionRange> ranges)
        {
            if (ranges == null || ranges.Count == 0)
                return "-";

            int count = math.min(3, ranges.Count);
            string value = string.Empty;
            for (int index = 0; index < count; index++)
            {
                TrackExtensionRange range = ranges[index];
                if (index > 0)
                    value += ";";
                value += range.StartAtomIndex + ">"
                    + range.ForwardEndAtomIndexExclusive + ">"
                    + range.ResumeAtomIndex;
            }
            return ranges.Count > count ? value + ";..." : value;
        }

        private bool TryAppendTramStopTurnback(
            DynamicBuffer<RouteWaypoint> waypoints,
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges,
            List<TrackExtensionRange> extensionRanges,
            int waypointIndex,
            Entity building,
            out bool candidate)
        {
            candidate = false;
            Entity waypoint = waypoints[waypointIndex].m_Waypoint;
            if (waypoint == Entity.Null
                || building != Entity.Null
                || !EntityManager.HasComponent<RouteLane>(waypoint))
            {
                return false;
            }

            Entity stop = m_Support.Stop(waypoint);
            if (stop == Entity.Null || !EntityManager.HasComponent<TransportStop>(stop))
                return false;

            RouteLane routeLane = EntityManager.GetComponentData<RouteLane>(waypoint);
            if (routeLane.m_StartLane == Entity.Null
                || routeLane.m_StartLane != routeLane.m_EndLane
                || !Approximately(routeLane.m_StartCurvePos, routeLane.m_EndCurvePos))
            {
                return false;
            }
            candidate = true;

            int incomingSegment = waypointIndex == 0 ? ranges.Count - 1 : waypointIndex - 1;
            TrackSegmentRange incomingRange = ranges[incomingSegment];
            TrackSegmentRange outgoingRange = ranges[waypointIndex];
            if (incomingRange.EndAtomIndexExclusive <= incomingRange.StartAtomIndex
                || incomingRange.EndAtomIndexExclusive > atoms.Count
                || outgoingRange.StartAtomIndex < 0
                || outgoingRange.StartAtomIndex >= outgoingRange.EndAtomIndexExclusive
                || outgoingRange.StartAtomIndex >= atoms.Count)
            {
                return false;
            }

            TrackAtom incoming = atoms[incomingRange.EndAtomIndexExclusive - 1];
            TrackAtom outgoing = atoms[outgoingRange.StartAtomIndex];
            Entity incomingLane = AtomLane(incoming);
            Entity outgoingLane = AtomLane(outgoing);
            if (incoming.AtomClass != TrackAtomClass.PrimaryLane
                || outgoing.AtomClass != TrackAtomClass.PrimaryLane
                || incomingLane != routeLane.m_StartLane
                || outgoingLane != routeLane.m_EndLane
                || !Approximately(incoming.TargetDelta.y, routeLane.m_StartCurvePos)
                || !Approximately(outgoing.TargetDelta.x, routeLane.m_EndCurvePos)
                || !TryGetAtomDirection(incoming, out bool incomingForward)
                || !TryGetAtomDirection(outgoing, out bool outgoingForward)
                || incomingForward == outgoingForward)
            {
                return false;
            }

            float endpoint = incomingForward ? 1f : 0f;
            var additions = new List<TrackAtom>(2);
            AppendStationAtom(
                additions,
                incomingLane,
                incoming.TargetDelta.y,
                endpoint,
                incomingLane,
                incomingLane);
            int forwardCount = additions.Count;
            AppendStationAtom(
                additions,
                incomingLane,
                endpoint,
                outgoing.TargetDelta.x,
                incomingLane,
                outgoingLane);
            if (forwardCount == 0 || additions.Count == forwardCount)
                return false;

            InsertExtension(
                atoms,
                ranges,
                extensionRanges,
                waypointIndex,
                outgoingRange.StartAtomIndex,
                additions,
                forwardCount);
            return true;
        }

        private bool TryBuildStationPath(
            TrackAtom incoming,
            Entity lane,
            bool forward,
            Entity building,
            float maxDistance,
            List<TrackAtom> stationPath,
            out bool reachedBoundary,
            TrackBuildInputs buildInputs)
        {
            reachedBoundary = false;
            if (lane == Entity.Null
                || !EntityManager.HasComponent<TrackLane>(lane)
                || !EntityManager.HasComponent<Lane>(lane)
                || (building != Entity.Null && m_Support.ResolvePassingStationBuilding(lane) != building))
                return false;

            float endpoint = forward ? 1f : 0f;
            float remainingDistance = maxDistance;
            if (!TryAppendBoundedStationAtom(
                    stationPath,
                    lane,
                    incoming.TargetDelta.y,
                    endpoint,
                    Entity.Null,
                    Entity.Null,
                    ref remainingDistance,
                    out bool limitReached))
            {
                return false;
            }
            if (limitReached)
            {
                reachedBoundary = true;
                return stationPath.Count > 0;
            }
            var visited = new List<Entity> { lane };
            Entity currentLane = lane;
            bool currentForward = forward;
            while (true)
            {
                if (!TryFindConnectedStationLane(
                    currentLane,
                    currentForward,
                    out Entity nextLane,
                    out bool nextForward,
                    buildInputs))
                {
                    reachedBoundary = true;
                    return stationPath.Count > 0;
                }
                if (visited.Contains(nextLane))
                    return false;
                if (building != Entity.Null
                    && m_Support.ResolvePassingStationBuilding(nextLane) != building)
                {
                    reachedBoundary = true;
                    return stationPath.Count > 0;
                }
                if (!EntityManager.HasComponent<TrackLane>(nextLane)
                    || !EntityManager.HasComponent<Lane>(nextLane))
                {
                    return false;
                }
                if (!TryAppendBoundedStationAtom(
                        stationPath,
                        nextLane,
                        nextForward ? 0f : 1f,
                        nextForward ? 1f : 0f,
                        currentLane,
                        Entity.Null,
                        ref remainingDistance,
                        out limitReached))
                {
                    return false;
                }
                visited.Add(nextLane);
                if (limitReached)
                {
                    reachedBoundary = true;
                    return stationPath.Count > 0;
                }
                currentLane = nextLane;
                currentForward = nextForward;
            }
        }

        private bool TryAppendBoundedStationAtom(
            List<TrackAtom> atoms,
            Entity lane,
            float start,
            float end,
            Entity previousLane,
            Entity nextLane,
            ref float remainingDistance,
            out bool limitReached)
        {
            limitReached = false;
            if (start == end)
                return true;
            if (math.isfinite(remainingDistance))
            {
                if (!EntityManager.HasComponent<Curve>(lane))
                    return false;
                float length = EntityManager.GetComponentData<Curve>(lane).m_Length
                    * math.abs(end - start);
                if (!math.isfinite(length) || length < 0f || remainingDistance <= 0f)
                    return false;
                if (length >= remainingDistance)
                {
                    end = math.lerp(start, end, remainingDistance / length);
                    remainingDistance = 0f;
                    limitReached = true;
                }
                else
                {
                    remainingDistance -= length;
                }
            }
            AppendStationAtom(atoms, lane, start, end, previousLane, nextLane);
            return true;
        }

        private static bool TryGetAtomDirection(TrackAtom atom, out bool forward)
        {
            float delta = atom.TargetDelta.y - atom.TargetDelta.x;
            forward = delta > 0f;
            return delta != 0f;
        }

        private static Entity AtomLane(TrackAtom atom)
        {
            return atom.Key.PhysicalLaneKey != Entity.Null
                ? atom.Key.PhysicalLaneKey
                : atom.SourceTarget;
        }

        private bool TryAppendStationPath(
            List<TrackAtom> stationPath,
            TrackAtom outgoing,
            bool outgoingForward,
            bool turnback,
            List<TrackAtom> additions,
            out int forwardCount,
            TrackBuildInputs buildInputs)
        {
            forwardCount = -1;
            if (stationPath == null || stationPath.Count == 0)
                return false;

            for (int index = 0; index < stationPath.Count; index++)
            {
                TrackAtom stationAtom = stationPath[index];
                if (stationAtom.Key.PhysicalLaneKey != outgoing.Key.PhysicalLaneKey)
                {
                    additions.Add(stationAtom);
                    continue;
                }

                if (TryGetAtomDirection(stationAtom, out bool stationForward)
                    && stationForward == outgoingForward
                    && (IsForwardGap(stationAtom.TargetDelta.x, outgoing.TargetDelta.x, stationForward)
                        || stationAtom.TargetDelta.x == outgoing.TargetDelta.x))
                {
                    AppendStationAtom(additions, stationAtom.Key.PhysicalLaneKey, stationAtom.TargetDelta.x, outgoing.TargetDelta.x, stationAtom.Key.PreviousTarget, Entity.Null);
                    return true;
                }
                additions.Clear();
                break;
            }

            if (additions.Count == stationPath.Count)
            {
                TrackAtom last = stationPath[stationPath.Count - 1];
                if (TryGetAtomDirection(last, out bool lastForward)
                    && TryFindConnectedStationLane(
                        last.Key.PhysicalLaneKey,
                        lastForward,
                        out Entity directLane,
                        out bool directForward,
                        buildInputs)
                    && directLane == outgoing.Key.PhysicalLaneKey
                    && directForward == outgoingForward)
                {
                    return true;
                }
                additions.Clear();
            }

            if (!turnback)
                return false;

            additions.AddRange(stationPath);
            forwardCount = additions.Count;
            for (int index = stationPath.Count - 1; index >= 0; index--)
            {
                TrackAtom stationAtom = stationPath[index];
                if (stationAtom.Key.PhysicalLaneKey != outgoing.Key.PhysicalLaneKey)
                {
                    AppendStationAtom(additions, stationAtom.Key.PhysicalLaneKey, stationAtom.TargetDelta.y, stationAtom.TargetDelta.x, Entity.Null, stationAtom.Key.NextTarget);
                    continue;
                }

                if (TryGetAtomDirection(stationAtom, out bool stationForward)
                    && stationForward != outgoingForward
                    && (IsForwardGap(stationAtom.TargetDelta.y, outgoing.TargetDelta.x, outgoingForward)
                        || stationAtom.TargetDelta.y == outgoing.TargetDelta.x))
                {
                    AppendStationAtom(additions, stationAtom.Key.PhysicalLaneKey, stationAtom.TargetDelta.y, outgoing.TargetDelta.x, Entity.Null, stationAtom.Key.NextTarget);
                    return true;
                }
                additions.Clear();
                forwardCount = -1;
                return false;
            }

            TrackAtom reverseLast = additions[additions.Count - 1];
            if (!TryGetAtomDirection(reverseLast, out bool reverseForward)
                || !TryFindConnectedStationLane(
                    reverseLast.Key.PhysicalLaneKey,
                    reverseForward,
                    out Entity connectedLane,
                    out bool connectedForward,
                    buildInputs)
                || connectedLane != outgoing.Key.PhysicalLaneKey
                || connectedForward != outgoingForward)
            {
                additions.Clear();
                forwardCount = -1;
                return false;
            }
            return true;
        }

        private void AppendStationAtom(
            List<TrackAtom> atoms,
            Entity lane,
            float start,
            float end,
            Entity previousLane,
            Entity nextLane)
        {
            if (start == end)
                return;
            atoms.Add(new TrackAtom(
                new TrackAtomKey(lane, previousLane, nextLane),
                lane,
                new float2(start, end),
                0,
                TrackAtomClass.PrimaryLane,
                ResolveStationTraversalDir(lane)));
        }

        private TrackTraversalDir ResolveStationTraversalDir(Entity lane)
        {
            if (lane == Entity.Null)
                return TrackTraversalDir.Unknown;
            if (EntityManager.HasComponent<EdgeLane>(lane))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(lane);
                bool direction = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                if (EntityManager.HasComponent<TrackLane>(lane)
                    && (EntityManager.GetComponentData<TrackLane>(lane).m_Flags & TrackLaneFlags.Invert) != 0)
                {
                    direction = !direction;
                }
                return direction ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }
            if (EntityManager.HasComponent<TrackLane>(lane))
            {
                return (EntityManager.GetComponentData<TrackLane>(lane).m_Flags & TrackLaneFlags.Invert) == 0
                    ? TrackTraversalDir.Forward
                    : TrackTraversalDir.Reverse;
            }
            return TrackTraversalDir.Unknown;
        }

        private static bool IsForwardGap(float start, float end, bool forward)
        {
            return forward ? end > start : end < start;
        }

        private static bool Approximately(float left, float right)
        {
            return math.isfinite(left)
                && math.isfinite(right)
                && math.abs(left - right) <= 1e-5f;
        }

        private static void InsertExtension(
            List<TrackAtom> atoms,
            List<TrackSegmentRange> ranges,
            List<TrackExtensionRange> extensionRanges,
            int segmentIndex,
            int insertIndex,
            List<TrackAtom> additions,
            int forwardCount)
        {
            atoms.InsertRange(insertIndex, additions);
            ShiftRangesAfterInsert(ranges, segmentIndex, additions.Count);
            ShiftExtensionRangesAfterInsert(extensionRanges, insertIndex, additions.Count);
            if (forwardCount >= 0)
            {
                extensionRanges.Add(new TrackExtensionRange(
                    insertIndex,
                    insertIndex + forwardCount,
                    insertIndex + additions.Count));
            }
        }

        private static void ShiftRangesAfterInsert(
            List<TrackSegmentRange> ranges,
            int segmentIndex,
            int count)
        {
            for (int index = segmentIndex; index < ranges.Count; index++)
            {
                TrackSegmentRange range = ranges[index];
                if (index == segmentIndex)
                {
                    ranges[index] = new TrackSegmentRange(range.StartAtomIndex, range.EndAtomIndexExclusive + count);
                }
                else
                {
                    ranges[index] = new TrackSegmentRange(range.StartAtomIndex + count, range.EndAtomIndexExclusive + count);
                }
            }
        }

        private static void ShiftExtensionRangesAfterInsert(
            List<TrackExtensionRange> ranges,
            int insertIndex,
            int count)
        {
            if (ranges == null || count <= 0)
                return;

            for (int index = 0; index < ranges.Count; index++)
            {
                TrackExtensionRange range = ranges[index];
                int start = range.StartAtomIndex >= insertIndex
                    ? range.StartAtomIndex + count
                    : range.StartAtomIndex;
                int forwardEnd = range.ForwardEndAtomIndexExclusive >= insertIndex
                    ? range.ForwardEndAtomIndexExclusive + count
                    : range.ForwardEndAtomIndexExclusive;
                int resume = range.ResumeAtomIndex >= insertIndex
                    ? range.ResumeAtomIndex + count
                    : range.ResumeAtomIndex;
                ranges[index] = new TrackExtensionRange(start, forwardEnd, resume);
            }
        }

        private bool TryFindConnectedStationLane(
            Entity lane,
            bool forward,
            out Entity nextLane,
            out bool nextForward,
            TrackBuildInputs buildInputs)
        {
            nextLane = Entity.Null;
            nextForward = false;
            if (lane == Entity.Null
                || !EntityManager.HasComponent<Lane>(lane)
                || !EntityManager.HasComponent<Owner>(lane))
            {
                return false;
            }

            Entity candidate = lane;
            bool candidateForward = forward;
            if (!NetUtils.FindConnectedLane(
                    ref candidate,
                    ref candidateForward,
                    ref buildInputs.Lanes,
                    ref buildInputs.EdgeLanes,
                    ref buildInputs.Owners,
                    ref buildInputs.Edges,
                    ref buildInputs.ConnectedEdges,
                    ref buildInputs.SubLanes))
            {
                return false;
            }

            nextLane = candidate;
            nextForward = candidateForward;
            return true;
        }
        internal TrackAtomClass ClassifyPathElementTarget(PathElement element)
        {
            bool hasConnectionLane = EntityManager.HasComponent<ConnectionLane>(element.m_Target);
            TrackTypes connectionTrackTypes = hasConnectionLane ? EntityManager.GetComponentData<ConnectionLane>(element.m_Target).m_TrackTypes : TrackTypes.None;
            return ClassifyPathElementTarget(
                element.m_Flags,
                EntityManager.HasComponent<TrackLane>(element.m_Target),
                hasConnectionLane,
                connectionTrackTypes,
                EntityManager.HasComponent<EdgeLane>(element.m_Target));
        }
        private void BuildJunctionMarkers(LineTrackChain chain)
        {
            chain.JunctionMarkers.Clear();
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                for (int atomIndex = range.StartAtomIndex;
                    atomIndex < range.EndAtomIndexExclusive;
                    atomIndex++)
                {
                    Entity lane = chain.TrackAtoms[atomIndex].SourceTarget;
                    if (!TryResolveJunction(lane, out _))
                        continue;

                    chain.JunctionMarkers.Add(new TrackJunctionMarker(
                        segmentIndex,
                        atomIndex,
                        lane));
                }
            }
        }
        private bool TryResolveJunction(Entity lane, out Entity intersection)
        {
            intersection = Entity.Null;
            if (lane == Entity.Null
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<Owner>(lane))
            {
                return false;
            }
            intersection = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            return intersection != Entity.Null
                && EntityManager.Exists(intersection)
                && EntityManager.HasComponent<Node>(intersection)
                && EntityManager.HasBuffer<ConnectedEdge>(intersection);
        }
        internal static TrackAtomClass ClassifyPathElementTarget(PathElementFlags flags, bool hasTrackLane, bool hasConnectionLane, TrackTypes connectionTrackTypes, bool hasEdgeLane)
        {
            if ((flags & (PathElementFlags.Action | PathElementFlags.WaitPosition | PathElementFlags.Hangaround)) != 0)
                return TrackAtomClass.FilteredNoise;
            if (hasTrackLane)
                return TrackAtomClass.PrimaryLane;
            if (hasConnectionLane && connectionTrackTypes != TrackTypes.None)
                return TrackAtomClass.ConnectionHelper;
            if (hasEdgeLane)
                return TrackAtomClass.ConnectionHelper;
            if ((flags & (PathElementFlags.Secondary | PathElementFlags.Return | PathElementFlags.Leader)) != 0)
                return TrackAtomClass.ConnectionHelper;
            return TrackAtomClass.PrimaryLane;
        }
        internal TrackTraversalDir ResolveTraversalDirection(DynamicBuffer<PathElement> pathElements, int pathIndex)
        {
            PathElement current = pathElements[pathIndex];
            if (current.m_Target == Entity.Null)
                return TrackTraversalDir.Unknown;
            bool reverseFlag = (current.m_Flags & PathElementFlags.Reverse) != 0;
            if (EntityManager.HasComponent<EdgeLane>(current.m_Target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(current.m_Target);
                bool edgeForward = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                if (EntityManager.HasComponent<TrackLane>(current.m_Target))
                {
                    TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                    if ((trackLane.m_Flags & TrackLaneFlags.Invert) != 0)
                        edgeForward = !edgeForward;
                }
                if (reverseFlag)
                    edgeForward = !edgeForward;
                return edgeForward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }
            if (EntityManager.HasComponent<TrackLane>(current.m_Target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                bool forward = (trackLane.m_Flags & TrackLaneFlags.Invert) == 0;
                if (reverseFlag)
                    forward = !forward;
                return forward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }
            if (pathElements.Length <= 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;
            float laneProgress = current.m_TargetDelta.y - current.m_TargetDelta.x;
            if (math.abs(laneProgress) > 0.0001f)
            {
                bool forwardByDelta = laneProgress >= 0f;
                if (reverseFlag)
                    forwardByDelta = !forwardByDelta;
                return forwardByDelta ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }
            if (pathIndex == 0 || pathIndex == pathElements.Length - 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;
            PathElement previous = pathElements[pathIndex - 1];
            PathElement next = pathElements[pathIndex + 1];
            if (previous.m_Target != Entity.Null && previous.m_Target == next.m_Target)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;
            return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Forward;
        }
        private void TryAppendControlPoint(
            List<ControlPointMarker> controlPoints,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            int atomIndex)
        {
            Entity building = m_Support.GetStationBuildingForWaypoint(waypoints, waypointIndex);
            if (building != Entity.Null)
            {
                ControlPointKind kind = m_Support.IsBypassStation(building)
                    ? ControlPointKind.Bypass
                    : ControlPointKind.Stop;
                controlPoints.Add(new ControlPointMarker(atomIndex, waypointIndex, building, kind));
                return;
            }
            if (TransportModeResolver.Resolve(EntityManager, line) != TransitMode.Tram)
                return;
            Entity stop = m_Support.Stop(waypoints[waypointIndex].m_Waypoint);
            if (stop != Entity.Null && EntityManager.HasComponent<TransportStop>(stop))
                controlPoints.Add(new ControlPointMarker(atomIndex, waypointIndex, stop, ControlPointKind.Stop));
        }
        private void TryAppendEndpointMarker(
            List<EndpointMarker> endpointMarkers,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            int atomIndex)
        {
            Entity waypoint = waypoints[waypointIndex].m_Waypoint;
            if (RouteWaypointEndpointResolver.TryResolveRouteWaypointEndpoint(EntityManager, waypoint, out RouteWaypointEndpoint endpoint))
            {
                endpointMarkers.Add(new EndpointMarker(atomIndex, waypointIndex, waypoint, endpoint.OutsideConnection, endpoint.Kind, endpoint.Direction));
            }
        }
        private void BuildAtomStationBuildings(LineTrackChain chain)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
            {
                if (chain != null)
                    chain.AtomStationBuildings = Array.Empty<Entity>();
                return;
            }
            Entity[] atomStationBuildings = new Entity[chain.TrackAtoms.Count];
            const int stationWindowAtoms = 3;
            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
            {
                ControlPointMarker controlPoint = chain.ControlPoints[controlPointIndex];
                if (controlPoint.Building == Entity.Null)
                    continue;
                if (EntityManager.HasComponent<TransportStop>(controlPoint.Building))
                    continue;
                int start = math.max(0, controlPoint.AtomIndex - stationWindowAtoms);
                int endExclusive = math.min(chain.TrackAtoms.Count, controlPoint.AtomIndex + stationWindowAtoms + 1);
                for (int atomIndex = start; atomIndex < endExclusive; atomIndex++)
                    atomStationBuildings[atomIndex] = controlPoint.Building;
            }
            chain.AtomStationBuildings = atomStationBuildings;
        }
        private void BuildControlEdges(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain.ControlPoints.Count < 2)
                return;
            float lineFrames = m_Support.GetLineLoopFramesEstimate(line, waypoints);
            int atomCount = math.max(1, chain.TrackAtoms.Count);
            bool hasProfile = m_Support.TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile);
            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count - 1; controlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[controlPointIndex];
                ControlPointMarker end = chain.ControlPoints[controlPointIndex + 1];
                int startAtomIndex = math.clamp(start.AtomIndex, 0, atomCount - 1);
                int endAtomIndexExclusive = math.clamp(math.max(startAtomIndex + 1, end.AtomIndex), 1, atomCount);
                float baseFrames = 0f;
                if (hasProfile)
                {
                    int startWaypointIndex = math.clamp(start.WaypointIndex, 0, waypoints.Length - 1);
                    int endWaypointIndex = math.clamp(end.WaypointIndex, 0, waypoints.Length - 1);
                    baseFrames = m_Support.ComputeDepartureToWaypointFramesFromProfile(profile, startWaypointIndex, endWaypointIndex);
                }
                if (!(baseFrames > 0f))
                {
                    float ratio = (endAtomIndexExclusive - startAtomIndex) / (float)atomCount;
                    baseFrames = lineFrames > 0f ? lineFrames * ratio : 0f;
                }
                chain.ControlEdges.Add(new ControlEdge(
                    controlPointIndex,
                    controlPointIndex + 1,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }
        }
        private void BuildAtomIndicesByLane(LineTrackChain chain)
        {
            if (chain == null)
                return;
            chain.AtomIndicesByLane.Clear();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                AddAtomIndexForLane(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForLane(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);
                AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);
            }
        }
        private static void AddAtomIndexForLane(Dictionary<Entity, List<int>> indexByLane, Entity lane, int atomIndex)
        {
            if (lane == Entity.Null)
                return;
            if (!indexByLane.TryGetValue(lane, out List<int> atomIndices))
            {
                atomIndices = new List<int>();
                indexByLane[lane] = atomIndices;
            }
            atomIndices.Add(atomIndex);
        }
        private void AddAtomIndexForNetOwnerChain(Dictionary<Entity, List<int>> indexByLane, Entity entity, int atomIndex)
        {
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (!EntityManager.HasComponent<Owner>(current))
                    break;
                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;
                if (EntityManager.HasComponent<Game.Net.Edge>(owner)
                    || EntityManager.HasComponent<Game.Net.Node>(owner)
                    || EntityManager.HasBuffer<Game.Net.SubLane>(owner))
                {
                    AddAtomIndexForLane(indexByLane, owner, atomIndex);
                }
                current = owner;
            }
        }
    }
}
