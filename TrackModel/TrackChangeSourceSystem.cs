using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    [System.Flags]
    internal enum TrackChangeKind : byte
    {
        None = 0,
        Path = 1 << 0,
        Layout = 1 << 1,
        Deleted = 1 << 2
    }

    internal readonly struct TrackChangeCandidate
    {
        internal readonly Entity Line;
        internal readonly TrackChangeKind Kind;
#if RT_DEBUG_TOOLS
        internal const int MaxSourceSegments = 16;
        internal readonly uint Frame;
        internal readonly Entity[] SourceSegments;
        internal readonly int SourceSegmentTotal;
        internal readonly bool SourceSegmentsExact;
#endif

#if RT_DEBUG_TOOLS
        internal TrackChangeCandidate(
            Entity line,
            TrackChangeKind kind,
            uint frame,
            Entity sourceSegment,
            bool sourceSegmentExact)
        {
            Line = line;
            Kind = kind;
            Frame = frame;
            SourceSegments = sourceSegment == Entity.Null
                ? System.Array.Empty<Entity>()
                : new[] { sourceSegment };
            SourceSegmentTotal = sourceSegment == Entity.Null ? 0 : 1;
            SourceSegmentsExact = sourceSegmentExact;
        }

        internal TrackChangeCandidate(
            Entity line,
            TrackChangeKind kind,
            uint frame,
            Entity[] sourceSegments,
            int sourceSegmentTotal,
            bool sourceSegmentsExact)
        {
            Line = line;
            Kind = kind;
            Frame = frame;
            SourceSegments = sourceSegments ?? System.Array.Empty<Entity>();
            SourceSegmentTotal = sourceSegmentTotal;
            SourceSegmentsExact = sourceSegmentsExact;
        }
#else
        internal TrackChangeCandidate(Entity line, TrackChangeKind kind)
        {
            Line = line;
            Kind = kind;
        }
#endif

        internal bool LayoutChanged => (Kind & TrackChangeKind.Layout) != 0;
    }

    internal sealed partial class TrackChangeSourceSystem : GameSystemBase
    {
        private readonly List<TrackChangeCandidate> m_PendingChanges =
            new List<TrackChangeCandidate>(128);
        private readonly Dictionary<Entity, int> m_PendingIndexes =
            new Dictionary<Entity, int>(128);
#if RT_DEBUG_TOOLS
        private SimulationSystem m_SimulationSystem;
#endif
        private EntityQuery m_LineChangeQuery;
        private EntityQuery m_LineDeletedQuery;
        private EntityQuery m_SegmentChangeQuery;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_LineChangeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<RouteSegment>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
            m_LineChangeQuery.SetChangedVersionFilter(new ComponentType[]
            {
                ComponentType.ReadOnly<RouteWaypoint>(),
                ComponentType.ReadOnly<RouteSegment>()
            });
            m_LineDeletedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<Deleted>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Temp>()
                }
            });
            m_LineDeletedQuery.SetChangedVersionFilter(new ComponentType[]
            {
                ComponentType.ReadOnly<Deleted>()
            });
            m_SegmentChangeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Segment>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PathElement>(),
                    ComponentType.ReadOnly<PathInformation>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_SegmentChangeQuery.SetChangedVersionFilter(new ComponentType[]
            {
                ComponentType.ReadOnly<PathElement>(),
                ComponentType.ReadOnly<PathInformation>()
            });
#if RT_DEBUG_TOOLS
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
#endif
        }

        protected override void OnUpdate()
        {
            CompleteDependency();
            m_LineChangeQuery.CompleteDependency();
            m_LineDeletedQuery.CompleteDependency();
            m_SegmentChangeQuery.CompleteDependency();
            CollectLines();
            CollectSegments();
            CollectDeletedLines();
        }

        internal void DrainChanges(List<TrackChangeCandidate> output)
        {
            if (output == null)
                return;

            output.Clear();
            if (m_PendingChanges.Count == 0)
                return;

            output.AddRange(m_PendingChanges);
            m_PendingChanges.Clear();
            m_PendingIndexes.Clear();
        }

        protected override void OnDestroy()
        {
            m_PendingChanges.Clear();
            m_PendingIndexes.Clear();
            base.OnDestroy();
        }

        private void CollectLines()
        {
            NativeArray<Entity> lines = m_LineChangeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
#if RT_DEBUG_TOOLS
                    AddChange(lines[i], TrackChangeKind.Layout, Entity.Null, false);
#else
                    AddChange(lines[i], TrackChangeKind.Layout);
#endif
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
        }

        private void CollectSegments()
        {
            NativeArray<Entity> segments = m_SegmentChangeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < segments.Length; i++)
                {
                    Owner owner = EntityManager.GetComponentData<Owner>(segments[i]);
#if RT_DEBUG_TOOLS
                    // Changed-version queries identify a candidate chunk, not the exact changed item.
                    AddChange(owner.m_Owner, TrackChangeKind.Path, segments[i], false);
#else
                    AddChange(owner.m_Owner, TrackChangeKind.Path);
#endif
                }
            }
            finally
            {
                if (segments.IsCreated)
                    segments.Dispose();
            }
        }

        private void CollectDeletedLines()
        {
            NativeArray<Entity> lines = m_LineDeletedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
#if RT_DEBUG_TOOLS
                    AddChange(lines[i], TrackChangeKind.Deleted, Entity.Null, true);
#else
                    AddChange(lines[i], TrackChangeKind.Deleted);
#endif
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
        }

#if RT_DEBUG_TOOLS
        private void AddChange(Entity line, TrackChangeKind kind, Entity sourceSegment, bool sourceSegmentExact)
#else
        private void AddChange(Entity line, TrackChangeKind kind)
#endif
        {
            if (line == Entity.Null)
                return;

            if (m_PendingIndexes.TryGetValue(line, out int index))
            {
                TrackChangeCandidate previous = m_PendingChanges[index];
#if RT_DEBUG_TOOLS
                Entity[] sourceSegments = previous.SourceSegments;
                int sourceSegmentTotal = previous.SourceSegmentTotal;
                bool containsSegment = sourceSegment == Entity.Null;
                for (int segmentIndex = 0; !containsSegment && segmentIndex < sourceSegments.Length; segmentIndex++)
                    containsSegment = sourceSegments[segmentIndex] == sourceSegment;
                if (!containsSegment)
                    sourceSegmentTotal++;
                if (!containsSegment && sourceSegments.Length < TrackChangeCandidate.MaxSourceSegments)
                {
                    Entity[] expanded = new Entity[sourceSegments.Length + 1];
                    for (int segmentIndex = 0; segmentIndex < sourceSegments.Length; segmentIndex++)
                        expanded[segmentIndex] = sourceSegments[segmentIndex];
                    expanded[sourceSegments.Length] = sourceSegment;
                    sourceSegments = expanded;
                }
                m_PendingChanges[index] = new TrackChangeCandidate(
                    line,
                    previous.Kind | kind,
                    m_SimulationSystem != null ? m_SimulationSystem.frameIndex : previous.Frame,
                    sourceSegments,
                    sourceSegmentTotal,
                    previous.SourceSegmentsExact || sourceSegmentExact);
#else
                m_PendingChanges[index] = new TrackChangeCandidate(line, previous.Kind | kind);
#endif
                return;
            }

            m_PendingIndexes.Add(line, m_PendingChanges.Count);
#if RT_DEBUG_TOOLS
            m_PendingChanges.Add(new TrackChangeCandidate(
                line,
                kind,
                m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u,
                sourceSegment,
                sourceSegmentExact));
#else
            m_PendingChanges.Add(new TrackChangeCandidate(line, kind));
#endif
        }
    }
}
