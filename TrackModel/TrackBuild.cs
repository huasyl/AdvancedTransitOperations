using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
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
        private readonly Action<Entity, ulong, ulong, int, int> m_NotifyLineTrackChainRebuilt;

        internal TrackBuild(
            TrackState state,
            TrackSupport support,
            TrackProfile profile,
            TrackDiag diag,
            Action markSharedDirty,
            Action<Entity, ulong, ulong, int, int> notifyLineTrackChainRebuilt)
        {
            m_State = state;
            m_Support = support;
            m_Profile = profile;
            m_Diag = diag;
            m_MarkSharedDirty = markSharedDirty;
            m_NotifyLineTrackChainRebuilt = notifyLineTrackChainRebuilt;
        }

        private EntityManager EntityManager => m_Support.EntityManager;

        internal static ulong MixLineTrackChainSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }

        private ulong ComputeLineTrackChainSignature(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineTrackChainSignature(hash, line.Index);
            hash = MixLineTrackChainSignature(hash, waypoints.Length);
            hash = MixLineTrackChainSignature(hash, segments.Length);

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
            }

            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                hash = MixLineTrackChainSignature(hash, segmentEntity.Index);

                if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
                    continue;

                DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                hash = MixLineTrackChainSignature(hash, pathElements.Length);
                for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
                {
                    PathElement pathElement = pathElements[pathIndex];
                    hash = MixLineTrackChainSignature(hash, pathElement.m_Target.Index);
                    hash = MixLineTrackChainSignature(hash, (int)pathElement.m_Flags);
                }
            }

            return hash;
        }

        internal bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            return TryGetChain(line, waypoints, out chain);
        }

        private bool TryGetChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null
                || waypoints.Length == 0
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                return false;
            }

            uint nowFrame = m_Support.FrameIndex;
            if (m_State.TryFrameSnapshot(line, out LineTrackChainFrameSnapshot frameSnapshot)
                && frameSnapshot.Frame == nowFrame
                && frameSnapshot.WaypointCount == waypoints.Length)
            {
                chain = frameSnapshot.Chain;
                return frameSnapshot.Available;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
            {
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null));
                return false;
            }

            ulong signature = ComputeLineTrackChainSignature(line, waypoints, segments);
            LineTrackChain previousChain = null;
            if (m_State.TryChain(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                bool available = chain.TrackAtoms.Count > 0;
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    available,
                    available ? chain : null));
                return available;
            }

            previousChain = chain;
            ulong previousSignature = previousChain != null ? previousChain.Signature : 0UL;
            int previousAtomCount = previousChain != null ? previousChain.TrackAtoms.Count : 0;
            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
            {
                m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null));
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
            m_State.PutFrameSnapshot(line, new LineTrackChainFrameSnapshot(
                nowFrame,
                waypoints.Length,
                true,
                chain));
            if (previousChain != null)
                m_NotifyLineTrackChainRebuilt?.Invoke(line, previousSignature, signature, previousAtomCount, chain.TrackAtoms.Count);
            m_Diag.AddDevSightChain(chain);
            m_MarkSharedDirty?.Invoke();
            return true;
        }

        private LineTrackChain BuildLineTrackChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature)
        {
            var chain = new LineTrackChain
            {
                LineEntity = line,
                Signature = signature
            };

            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int startAtomIndex = chain.TrackAtoms.Count;
                Entity segmentEntity = segments[waypointIndex].m_Segment;
                if (EntityManager.HasBuffer<PathElement>(segmentEntity))
                {
                    DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                    AppendSegmentTrackAtoms(chain.TrackAtoms, pathElements);
                }

                int endAtomIndexExclusive = chain.TrackAtoms.Count;
                chain.SegmentRanges.Add(new TrackSegmentRange(startAtomIndex, endAtomIndexExclusive));
                TryAppendControlPoint(chain.ControlPoints, line, waypoints, waypointIndex, startAtomIndex);
                TryAppendEndpointMarker(chain.EndpointMarkers, waypoints, waypointIndex, startAtomIndex);
            }

            BuildAtomStationBuildings(chain);
            BuildControlEdges(chain, line, waypoints);
            TransitMode mode = TransportModeResolver.Resolve(EntityManager, line);
            if (mode == TransitMode.Tram)
                BuildAtomIndicesByLane(chain);
            m_Profile.BuildTraversalProfile(chain, line, waypoints);
            m_Profile.BuildTurnbackBoundaries(chain, line, waypoints);
            m_Profile.LogTrackModelTurnbackBuild(chain);
            if (mode != TransitMode.Tram)
                BuildAtomIndicesByLane(chain);
            return chain;
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

        private void AppendSegmentTrackAtoms(List<TrackAtom> atoms, DynamicBuffer<PathElement> pathElements)
        {
            if (pathElements.Length == 0)
                return;

            for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
            {
                if (!TryClassifyTrackAtom(pathElements, pathIndex, out TrackAtom atom))
                    continue;

                if (atom.AtomClass == TrackAtomClass.FilteredNoise)
                    continue;

                atoms.Add(atom);
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
