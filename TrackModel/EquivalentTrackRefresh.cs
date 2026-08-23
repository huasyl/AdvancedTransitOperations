using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;
using NetTrackLane = Game.Net.TrackLane;
using NetConnectionLane = Game.Net.ConnectionLane;
namespace RapidTransitMod.TrackModel
{
    internal readonly struct TrackWaypointInputBaseline
    {
        internal readonly Entity Waypoint;
        internal readonly bool HasRouteLane;
        internal readonly int StartLaneIndex;
        internal readonly int EndLaneIndex;
        internal readonly int StartCurveScaled;
        internal readonly int EndCurveScaled;
        internal TrackWaypointInputBaseline(Entity waypoint, EntityManager entities)
        {
            Waypoint = waypoint;
            HasRouteLane = false;
            StartLaneIndex = 0;
            EndLaneIndex = 0;
            StartCurveScaled = 0;
            EndCurveScaled = 0;
            if (waypoint == Entity.Null || !entities.HasComponent<RouteLane>(waypoint))
                return;
            RouteLane routeLane = entities.GetComponentData<RouteLane>(waypoint);
            HasRouteLane = true;
            StartLaneIndex = routeLane.m_StartLane.Index;
            EndLaneIndex = routeLane.m_EndLane.Index;
            StartCurveScaled = (int)math.round(routeLane.m_StartCurvePos * 1000f);
            EndCurveScaled = (int)math.round(routeLane.m_EndCurvePos * 1000f);
        }
        internal bool EqualsCurrent(Entity waypoint, EntityManager entities)
        {
            TrackWaypointInputBaseline current = new TrackWaypointInputBaseline(waypoint, entities);
            return Waypoint.Index == current.Waypoint.Index
                && HasRouteLane == current.HasRouteLane
                && StartLaneIndex == current.StartLaneIndex
                && EndLaneIndex == current.EndLaneIndex
                && StartCurveScaled == current.StartCurveScaled
                && EndCurveScaled == current.EndCurveScaled;
        }
    }
    internal readonly struct TrackSegmentInputBaseline
    {
        internal readonly Entity Segment;
        internal readonly bool PathComplete;
        internal readonly TrackPathElementBaseline[] Elements;
        internal TrackSegmentInputBaseline(
            Entity segment,
            bool pathComplete,
            TrackPathElementBaseline[] elements)
        {
            Segment = segment;
            PathComplete = pathComplete;
            Elements = elements ?? Array.Empty<TrackPathElementBaseline>();
        }
    }
    internal readonly struct TrackPathElementBaseline
    {
        internal readonly Entity Target;
        internal readonly int TargetDeltaXBits;
        internal readonly int TargetDeltaYBits;
        internal readonly PathElementFlags Flags;
        internal readonly bool HasAtomContribution;
        internal readonly TrackAtomClass AtomClass;
        internal readonly TrackTraversalDir TraversalDir;
        internal TrackPathElementBaseline(
            PathElement element,
            TrackAtomClass atomClass,
            TrackTraversalDir traversalDir,
            bool hasAtomContribution)
        {
            Target = element.m_Target;
            TargetDeltaXBits = math.asint(element.m_TargetDelta.x);
            TargetDeltaYBits = math.asint(element.m_TargetDelta.y);
            Flags = element.m_Flags;
            HasAtomContribution = hasAtomContribution;
            AtomClass = hasAtomContribution ? atomClass : TrackAtomClass.Unknown;
            TraversalDir = hasAtomContribution ? traversalDir : TrackTraversalDir.Unknown;
        }
        internal bool StableEquals(
            TrackTargetComparer comparer,
            PathElement element,
            TrackAtomClass atomClass,
            TrackTraversalDir traversalDir,
            bool hasAtomContribution)
        {
            bool targetChanged = Target != element.m_Target;
            return TargetDeltaXBits == math.asint(element.m_TargetDelta.x)
                && TargetDeltaYBits == math.asint(element.m_TargetDelta.y)
                && Flags == element.m_Flags
                && HasAtomContribution == hasAtomContribution
                && (!hasAtomContribution || (AtomClass == atomClass && TraversalDir == traversalDir))
                && (!targetChanged || (comparer != null && comparer.AreEquivalent(Target, element.m_Target)));
        }
    }
    internal readonly struct TrackPathNodeFingerprint : IEquatable<TrackPathNodeFingerprint>
    {
        internal readonly int OwnerIndex;
        internal readonly int LaneIndex;
        internal readonly int CurveBits;
        internal readonly bool Secondary;
        internal TrackPathNodeFingerprint(PathNode node)
        {
            OwnerIndex = node.GetOwnerIndex();
            LaneIndex = node.GetLaneIndex();
            CurveBits = math.asint(node.GetCurvePos());
            Secondary = node.IsSecondary();
        }
        public bool Equals(TrackPathNodeFingerprint other)
        {
            return OwnerIndex == other.OwnerIndex
                && LaneIndex == other.LaneIndex
                && CurveBits == other.CurveBits
                && Secondary == other.Secondary;
        }
        public override bool Equals(object obj) => obj is TrackPathNodeFingerprint other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = OwnerIndex;
                hash = (hash * 397) ^ LaneIndex;
                hash = (hash * 397) ^ CurveBits;
                return (hash * 397) ^ (Secondary ? 1 : 0);
            }
        }
    }
    internal readonly struct TrackTargetFingerprint : IEquatable<TrackTargetFingerprint>
    {
        internal readonly bool Comparable;
        internal readonly bool HasOwner;
        internal readonly Entity Owner;
        internal readonly bool HasEdge;
        internal readonly Entity EdgeStart;
        internal readonly Entity EdgeEnd;
        internal readonly bool HasLane;
        internal readonly TrackPathNodeFingerprint StartNode;
        internal readonly TrackPathNodeFingerprint EndNode;
        internal readonly bool HasEdgeLane;
        internal readonly int EdgeDeltaXBits;
        internal readonly int EdgeDeltaYBits;
        internal readonly byte ConnectedStartCount;
        internal readonly byte ConnectedEndCount;
        internal readonly bool HasTrackLane;
        internal readonly Entity TrackAccessRestriction;
        internal readonly TrackLaneFlags TrackFlags;
        internal readonly int TrackSpeedBits;
        internal readonly int TrackCurvinessBits;
        internal readonly bool HasConnectionLane;
        internal readonly Entity ConnectionAccessRestriction;
        internal readonly ConnectionLaneFlags ConnectionFlags;
        internal readonly TrackTypes ConnectionTrackTypes;
        internal readonly RoadTypes ConnectionRoadTypes;
        internal readonly bool HasNodeLane;
        internal readonly int NodeWidthStartBits;
        internal readonly int NodeWidthEndBits;
        internal readonly NodeLaneFlags NodeFlags;
        internal readonly byte SharedStartCount;
        internal readonly byte SharedEndCount;
        internal readonly bool HasCurve;
        internal readonly int[] CurveBits;
        internal readonly int CurveLengthBits;
        internal readonly bool HasPrefab;
        internal readonly Entity Prefab;
        private TrackTargetFingerprint(EntityManager entities, Entity target)
        {
            Comparable = false;
            HasOwner = entities.HasComponent<Owner>(target);
            Owner = HasOwner ? entities.GetComponentData<Owner>(target).m_Owner : Entity.Null;
            HasEdge = entities.HasComponent<NetEdge>(target);
            EdgeStart = HasEdge ? entities.GetComponentData<NetEdge>(target).m_Start : Entity.Null;
            EdgeEnd = HasEdge ? entities.GetComponentData<NetEdge>(target).m_End : Entity.Null;
            HasLane = entities.HasComponent<Lane>(target);
            // Lane.m_MiddleNode.m_SearchKey is runtime-sensitive and is intentionally excluded.
            StartNode = HasLane ? new TrackPathNodeFingerprint(entities.GetComponentData<Lane>(target).m_StartNode) : default;
            EndNode = HasLane ? new TrackPathNodeFingerprint(entities.GetComponentData<Lane>(target).m_EndNode) : default;
            HasEdgeLane = entities.HasComponent<EdgeLane>(target);
            EdgeLane edgeLane = HasEdgeLane ? entities.GetComponentData<EdgeLane>(target) : default;
            EdgeDeltaXBits = HasEdgeLane ? math.asint(edgeLane.m_EdgeDelta.x) : 0;
            EdgeDeltaYBits = HasEdgeLane ? math.asint(edgeLane.m_EdgeDelta.y) : 0;
            ConnectedStartCount = edgeLane.m_ConnectedStartCount;
            ConnectedEndCount = edgeLane.m_ConnectedEndCount;
            HasTrackLane = entities.HasComponent<NetTrackLane>(target);
            NetTrackLane trackLane = HasTrackLane ? entities.GetComponentData<NetTrackLane>(target) : default;
            TrackAccessRestriction = trackLane.m_AccessRestriction;
            TrackFlags = trackLane.m_Flags;
            TrackSpeedBits = math.asint(trackLane.m_SpeedLimit);
            TrackCurvinessBits = math.asint(trackLane.m_Curviness);
            HasConnectionLane = entities.HasComponent<NetConnectionLane>(target);
            NetConnectionLane connectionLane = HasConnectionLane ? entities.GetComponentData<NetConnectionLane>(target) : default;
            ConnectionAccessRestriction = connectionLane.m_AccessRestriction;
            ConnectionFlags = connectionLane.m_Flags;
            ConnectionTrackTypes = connectionLane.m_TrackTypes;
            ConnectionRoadTypes = connectionLane.m_RoadTypes;
            HasNodeLane = entities.HasComponent<NodeLane>(target);
            NodeLane nodeLane = HasNodeLane ? entities.GetComponentData<NodeLane>(target) : default;
            NodeWidthStartBits = math.asint(nodeLane.m_WidthOffset.x);
            NodeWidthEndBits = math.asint(nodeLane.m_WidthOffset.y);
            NodeFlags = nodeLane.m_Flags;
            SharedStartCount = nodeLane.m_SharedStartCount;
            SharedEndCount = nodeLane.m_SharedEndCount;
            HasCurve = entities.HasComponent<Curve>(target);
            Curve curve = HasCurve ? entities.GetComponentData<Curve>(target) : default;
            CurveBits = HasCurve ? CurveValues(curve) : Array.Empty<int>();
            CurveLengthBits = HasCurve ? math.asint(curve.m_Length) : 0;
            HasPrefab = entities.HasComponent<PrefabRef>(target);
            Prefab = HasPrefab ? entities.GetComponentData<PrefabRef>(target).m_Prefab : Entity.Null;
            Comparable = HasOwner || HasEdge || HasLane || HasEdgeLane || HasTrackLane
                || HasConnectionLane || HasNodeLane || HasCurve || HasPrefab;
        }
        internal static TrackTargetFingerprint Capture(EntityManager entities, Entity target)
        {
            if (target == Entity.Null || !entities.Exists(target))
                return default;
            return new TrackTargetFingerprint(entities, target);
        }
        public bool Equals(TrackTargetFingerprint other)
        {
            return Comparable && other.Comparable
                && HasOwner == other.HasOwner && (!HasOwner || Owner == other.Owner)
                && HasEdge == other.HasEdge && (!HasEdge || (EdgeStart == other.EdgeStart && EdgeEnd == other.EdgeEnd))
                && HasLane == other.HasLane && (!HasLane || (StartNode.Equals(other.StartNode) && EndNode.Equals(other.EndNode)))
                && HasEdgeLane == other.HasEdgeLane && (!HasEdgeLane || (EdgeDeltaXBits == other.EdgeDeltaXBits && EdgeDeltaYBits == other.EdgeDeltaYBits && ConnectedStartCount == other.ConnectedStartCount && ConnectedEndCount == other.ConnectedEndCount))
                && HasTrackLane == other.HasTrackLane && (!HasTrackLane || (TrackAccessRestriction == other.TrackAccessRestriction && TrackFlags == other.TrackFlags && TrackSpeedBits == other.TrackSpeedBits && TrackCurvinessBits == other.TrackCurvinessBits))
                && HasConnectionLane == other.HasConnectionLane && (!HasConnectionLane || (ConnectionAccessRestriction == other.ConnectionAccessRestriction && ConnectionFlags == other.ConnectionFlags && ConnectionTrackTypes == other.ConnectionTrackTypes && ConnectionRoadTypes == other.ConnectionRoadTypes))
                && HasNodeLane == other.HasNodeLane && (!HasNodeLane || (NodeWidthStartBits == other.NodeWidthStartBits && NodeWidthEndBits == other.NodeWidthEndBits && NodeFlags == other.NodeFlags && SharedStartCount == other.SharedStartCount && SharedEndCount == other.SharedEndCount))
                && HasCurve == other.HasCurve && (!HasCurve || (CurveLengthBits == other.CurveLengthBits && CurveEquals(CurveBits, other.CurveBits)))
                && HasPrefab == other.HasPrefab && (!HasPrefab || Prefab == other.Prefab);
        }
        public override bool Equals(object obj) => obj is TrackTargetFingerprint other && Equals(other);
        public override int GetHashCode() => Comparable ? (Owner.GetHashCode() ^ TrackFlags.GetHashCode() ^ CurveLengthBits) : 0;
        private static int[] CurveValues(Curve curve)
        {
            return new[]
            {
                math.asint(curve.m_Bezier.a.x), math.asint(curve.m_Bezier.a.y), math.asint(curve.m_Bezier.a.z),
                math.asint(curve.m_Bezier.b.x), math.asint(curve.m_Bezier.b.y), math.asint(curve.m_Bezier.b.z),
                math.asint(curve.m_Bezier.c.x), math.asint(curve.m_Bezier.c.y), math.asint(curve.m_Bezier.c.z),
                math.asint(curve.m_Bezier.d.x), math.asint(curve.m_Bezier.d.y), math.asint(curve.m_Bezier.d.z)
            };
        }
        private static bool CurveEquals(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }
    }
    internal static class EquivalentTrackRefresh
    {
        internal static void RefreshTraversalLaneKeys(LineTrackChain chain)
        {
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices == null)
            {
                return;
            }
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                chain.TraversalProfile.RunSlices[sliceIndex] = new TraversalRunSlice(
                    slice.SliceIndex,
                    slice.StartAtomIndex,
                    slice.EndAtomIndexExclusive,
                    slice.StartEventIndex,
                    slice.EndEventIndex,
                    CollectPhysicalLaneKeys(chain, slice.StartAtomIndex, slice.EndAtomIndexExclusive),
                    slice.RunFrames);
            }
        }
        private static Entity[] CollectPhysicalLaneKeys(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain.TrackAtoms.Count == 0)
                return Array.Empty<Entity>();
            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeBoundary(startAtomIndex, atomCount);
            int end = NormalizeBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return Array.Empty<Entity>();
            HashSet<Entity> seen = new HashSet<Entity>();
            List<Entity> ordered = new List<Entity>();
            if (start < end)
            {
                AppendPhysicalLaneKeys(chain, start, end, seen, ordered);
            }
            else
            {
                AppendPhysicalLaneKeys(chain, start, atomCount, seen, ordered);
                AppendPhysicalLaneKeys(chain, 0, end, seen, ordered);
            }
            return ordered.Count == 0 ? Array.Empty<Entity>() : ordered.ToArray();
        }
        private static void AppendPhysicalLaneKeys(
            LineTrackChain chain,
            int start,
            int endExclusive,
            HashSet<Entity> seen,
            List<Entity> ordered)
        {
            for (int atomIndex = start; atomIndex < endExclusive; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass == TrackAtomClass.PrimaryLane
                    && atom.Key.PhysicalLaneKey != Entity.Null
                    && seen.Add(atom.Key.PhysicalLaneKey))
                {
                    ordered.Add(atom.Key.PhysicalLaneKey);
                }
            }
        }
        private static int NormalizeBoundary(int value, int atomCount)
        {
            if (atomCount <= 0)
                return 0;
            int normalized = value % atomCount;
            return normalized < 0 ? normalized + atomCount : normalized;
        }
    }
    internal sealed class TrackTargetComparer
    {
        private readonly EntityManager m_Entities;
        private readonly Dictionary<Entity, TrackTargetFingerprint> m_Fingerprints =
            new Dictionary<Entity, TrackTargetFingerprint>();
        internal TrackTargetComparer(EntityManager entities)
        {
            m_Entities = entities;
        }
        internal bool AreEquivalent(Entity oldTarget, Entity newTarget)
        {
            if (oldTarget == newTarget)
                return true;
            if (oldTarget == Entity.Null
                || newTarget == Entity.Null
                || !m_Entities.Exists(oldTarget)
                || !m_Entities.Exists(newTarget))
            {
                return false;
            }
            TrackTargetFingerprint oldFingerprint = Capture(oldTarget);
            TrackTargetFingerprint newFingerprint = Capture(newTarget);
            return oldFingerprint.Comparable
                && newFingerprint.Comparable
                && oldFingerprint.Equals(newFingerprint);
        }
        private TrackTargetFingerprint Capture(Entity target)
        {
            if (!m_Fingerprints.TryGetValue(target, out TrackTargetFingerprint fingerprint))
            {
                fingerprint = TrackTargetFingerprint.Capture(m_Entities, target);
                m_Fingerprints[target] = fingerprint;
            }
            return fingerprint;
        }
    }
}
