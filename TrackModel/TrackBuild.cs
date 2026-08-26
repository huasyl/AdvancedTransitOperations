using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
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
        private readonly struct TrackChainScan
        {
            internal readonly List<TrackAtom> Atoms;
            internal readonly List<TrackSegmentRange> Ranges;
            internal readonly TrackWaypointInputBaseline[] WaypointInputs;
            internal readonly TrackSegmentInputBaseline[] SegmentInputs;
            internal readonly bool ChainComplete;
            internal bool HasData => Atoms != null
                && Ranges != null
                && WaypointInputs != null
                && SegmentInputs != null;
            internal TrackChainScan(
                List<TrackAtom> atoms,
                List<TrackSegmentRange> ranges,
                TrackWaypointInputBaseline[] waypointInputs,
                TrackSegmentInputBaseline[] segmentInputs,
                bool chainComplete)
            {
                Atoms = atoms;
                Ranges = ranges;
                WaypointInputs = waypointInputs;
                SegmentInputs = segmentInputs;
                ChainComplete = chainComplete;
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
            out bool scanComplete,
            out bool scanUnchanged)
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
            scanComplete = false;
            scanUnchanged = false;
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
            hash = MixLineTrackChainSignature(hash, chainComplete ? 1 : 0);
            signature = hash;
            scanComplete = chainComplete
                && refreshedAtoms.Count > 0
                && refreshedRanges.Count == segments.Length;
            // The signature omits target components; keep the scan verdict for same-hash changes.
            if (!canCompare || !structureEqual)
                return false;
            if (refreshedAtoms.Count != previousChain.TrackAtoms.Count
                || refreshedRanges.Count != previousChain.SegmentRanges.Count)
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
            if (!EntityManager.Exists(line)
                || waypoints.Length == 0
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                InvalidateUnavailableChain(line, nowFrame, waypoints.Length);
                return false;
            }
            if (m_State.TryFrameSnapshot(line, out LineTrackChainFrameSnapshot frameSnapshot)
                && frameSnapshot.Frame == nowFrame
                && frameSnapshot.WaypointCount == waypoints.Length)
            {
                chain = frameSnapshot.Chain;
                return frameSnapshot.Available;
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
            LineTrackChain previousChain = null;
            m_State.TryChain(line, out previousChain);
            chain = previousChain;
            ulong signature;
            bool equivalentRefresh = false;
            List<TrackAtom> refreshedAtoms = null;
            List<TrackSegmentRange> refreshedRanges = null;
            TrackWaypointInputBaseline[] refreshedWaypointInputs = null;
            TrackSegmentInputBaseline[] refreshedSegmentInputs = null;
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
                out scanComplete,
                out scanUnchanged);
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
                TrackIntervals.ResetBypassPipeline(previousChain);
                previousChain.LocalBypassWaypointScenes = Array.Empty<LocalBypassWaypointSceneBinding>();
                previousChain.LocalBypassWaypointScenesVersion = 0;
                BuildAtomIndicesByLane(previousChain);
                EquivalentTrackRefresh.RefreshTraversalLaneKeys(previousChain);
                m_ClearStaticCachesForLine?.Invoke(line);
                m_Profile.RegisterTramLine(line, previousChain, waypoints);
                m_State.PutChain(line, previousChain);
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    true,
                    previousChain));
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
                scanComplete);
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
                SegmentRanges = scan.Ranges
            };
            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int startAtomIndex = chain.SegmentRanges[waypointIndex].StartAtomIndex;
                TryAppendControlPoint(chain.ControlPoints, line, waypoints, waypointIndex, startAtomIndex);
                TryAppendEndpointMarker(chain.EndpointMarkers, waypoints, waypointIndex, startAtomIndex);
            }
            BuildAtomStationBuildings(chain);
            BuildControlEdges(chain, line, waypoints);
            BuildAtomIndicesByLane(chain);
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
