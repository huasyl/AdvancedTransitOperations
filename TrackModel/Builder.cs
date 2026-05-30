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
    internal sealed class TrackModelBuilder
    {
        private uint m_Version = 1;
        private bool m_Dirty = true;

        public Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> Track { get; } = new Dictionary<TrackAtomKey, List<SharedTrackOccurrence>>();
        public Dictionary<Entity, List<SharedPhysicalOccurrence>> Physical { get; } = new Dictionary<Entity, List<SharedPhysicalOccurrence>>();

        public void Clear()
        {
            Track.Clear();
            Physical.Clear();
            m_Dirty = true;
        }

        public bool Dirty()
        {
            return m_Dirty;
        }

        public void MarkDirty()
        {
            m_Dirty = true;
        }

        public void ClearDirty()
        {
            m_Dirty = false;
        }

        public uint Version()
        {
            return m_Version;
        }

        public void Bump()
        {
            m_Version++;
        }

        public bool TryTrack(TrackAtomKey key, out List<SharedTrackOccurrence> occurrences)
        {
            return Track.TryGetValue(key, out occurrences);
        }

        public bool TryPhysical(Entity physicalLane, out List<SharedPhysicalOccurrence> occurrences)
        {
            return Physical.TryGetValue(physicalLane, out occurrences);
        }
    }

    internal sealed partial class TrackModelService
    {
        private ulong ComputeLineTrackChainSignature(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, line.Index);
            hash = MixLineSignature(hash, waypoints.Length);
            hash = MixLineSignature(hash, segments.Length);

            for (int i = 0; i < waypoints.Length; i++)
                hash = MixLineSignature(hash, waypoints[i].m_Waypoint.Index);

            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                hash = MixLineSignature(hash, segmentEntity.Index);

                if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
                    continue;

                DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                hash = MixLineSignature(hash, pathElements.Length);
                for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
                {
                    PathElement pathElement = pathElements[pathIndex];
                    hash = MixLineSignature(hash, pathElement.m_Target.Index);
                    hash = MixLineSignature(hash, (int)pathElement.m_Flags);
                }
            }

            return hash;
        }

        internal bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            return m_Coordinator.Ensure(line, waypoints, out chain, TryGetChain);
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

            uint nowFrame = m_Runtime.FrameIndex;
            if (m_LineTrackChainFrameSnapshots.TryGetValue(line, out LineTrackChainFrameSnapshot frameSnapshot)
                && frameSnapshot.Frame == nowFrame
                && frameSnapshot.WaypointCount == waypoints.Length)
            {
                chain = frameSnapshot.Chain;
                return frameSnapshot.Available;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
            {
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null);
                return false;
            }

            ulong signature = ComputeLineTrackChainSignature(line, waypoints, segments);
            LineTrackChain previousChain = null;
            if (m_Store.Get(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                bool available = chain.TrackAtoms.Count > 0;
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    available,
                    available ? chain : null);
                return available;
            }

            previousChain = chain;

            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
            {
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null);
                return false;
            }

            if (previousChain != null)
                RemoveDevSightLaneIndexForChain(previousChain);
            m_Store.Put(line, chain);
            m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                nowFrame,
                waypoints.Length,
                true,
                chain);
            AddDevSightLaneIndexForChain(chain);
            m_Builder.MarkDirty();
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
                TryAppendControlPoint(chain.ControlPoints, waypoints, waypointIndex, startAtomIndex);
            }

            BuildControlEdges(chain, line, waypoints);
            BuildTraversalProfile(chain, line, waypoints);
            BuildTurnbackBoundaries(chain, line, waypoints);
            LogTrackModelTurnbackBuild(chain);
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

        private TrackAtomClass ClassifyPathElementTarget(PathElement element)
        {
            if ((element.m_Flags & (PathElementFlags.Action | PathElementFlags.WaitPosition | PathElementFlags.Hangaround)) != 0)
                return TrackAtomClass.FilteredNoise;

            if (EntityManager.HasComponent<TrackLane>(element.m_Target))
                return TrackAtomClass.PrimaryLane;

            if (EntityManager.HasComponent<ConnectionLane>(element.m_Target))
            {
                ConnectionLane connectionLane = EntityManager.GetComponentData<ConnectionLane>(element.m_Target);
                if (connectionLane.m_TrackTypes != TrackTypes.None)
                    return TrackAtomClass.ConnectionHelper;
            }

            if (EntityManager.HasComponent<EdgeLane>(element.m_Target))
                return TrackAtomClass.ConnectionHelper;

            if ((element.m_Flags & (PathElementFlags.Secondary | PathElementFlags.Return | PathElementFlags.Leader)) != 0)
                return TrackAtomClass.ConnectionHelper;

            return TrackAtomClass.PrimaryLane;
        }

        private TrackTraversalDir ResolveTraversalDirection(DynamicBuffer<PathElement> pathElements, int pathIndex)
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
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            int atomIndex)
        {
            Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
            if (building == Entity.Null)
                return;

            ControlPointKind kind = ControlPointKind.Stop;
            if (m_Runtime.IsBypassStation(building))
                kind = ControlPointKind.Bypass;

            controlPoints.Add(new ControlPointMarker(atomIndex, waypointIndex, building, kind));
        }

        private void BuildControlEdges(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain.ControlPoints.Count < 2)
                return;

            float lineFrames = GetLineLoopFramesEstimate(line, waypoints);
            int atomCount = math.max(1, chain.TrackAtoms.Count);
            bool hasProfile = TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile);

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
                    baseFrames = ComputeDepartureToWaypointFramesFromProfile(profile, startWaypointIndex, endWaypointIndex);
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
                PathElement element = pathElements[pathIndex];
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

        private void AddDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || entry.Value == null
                    || entry.Value.Count == 0)
                    continue;

                if (!m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences))
                {
                    occurrences = new List<DevSightLaneOccurrence>();
                    m_DevSightLaneIndex[entry.Key] = occurrences;
                }

                occurrences.Add(new DevSightLaneOccurrence(chain.LineEntity, chain, entry.Value));
            }
        }

        private void RemoveDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || !m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                for (int i = occurrences.Count - 1; i >= 0; i--)
                {
                    if (occurrences[i].LineEntity == chain.LineEntity)
                        occurrences.RemoveAt(i);
                }

                if (occurrences.Count == 0)
                    m_DevSightLaneIndex.Remove(entry.Key);
            }
        }


    }
}
