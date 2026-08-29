using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.Tools;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    [System.Flags]
    internal enum LineChangeKind : byte
    {
        None = 0,
        Path = 1 << 0,
        Layout = 1 << 1,
        Deleted = 1 << 2
    }

    internal readonly struct LineChangeCandidate
    {
        internal readonly Entity Line;
        internal readonly LineChangeKind Kind;
        internal readonly TrackLineDeletedFact DeletedFact;

        internal LineChangeCandidate(Entity line, LineChangeKind kind)
        {
            Line = line;
            Kind = kind;
            DeletedFact = default;
        }

        internal LineChangeCandidate(TrackLineDeletedFact deletedFact)
        {
            Line = deletedFact.Line;
            Kind = LineChangeKind.Deleted;
            DeletedFact = deletedFact;
        }

        internal bool IsDeleted => (Kind & LineChangeKind.Deleted) != 0;
        internal bool LayoutChanged => (Kind & LineChangeKind.Layout) != 0;
    }

    internal sealed partial class LineChangeSourceSystem : GameSystemBase
    {
        private readonly List<LineChangeCandidate> m_PendingChanges =
            new List<LineChangeCandidate>(128);
        private readonly Dictionary<Entity, int> m_PendingIndexes =
            new Dictionary<Entity, int>(128);
        private readonly HashSet<Entity> m_AcknowledgedRoadDeletes = new HashSet<Entity>();
        private readonly List<Entity> m_RoadDeletePruneBuffer = new List<Entity>();
        private EntityQuery m_UpdatedLineQuery;
        private EntityQuery m_PathUpdatedQuery;
        private EntityQuery m_DeletedLineQuery;
        private Func<Entity, LineKey> m_StableKey;
        private Action<bool> m_CreatedLines;
        private uint m_RoadDeletePruneTick;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_UpdatedLineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<Updated>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
            m_PathUpdatedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Event>(),
                    ComponentType.ReadOnly<PathUpdated>()
                }
            });
            m_DeletedLineQuery = GetEntityQuery(new EntityQueryDesc
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
        }

        internal void BindStableKey(Func<Entity, LineKey> stableKey)
        {
            m_StableKey = stableKey ?? throw new ArgumentNullException(nameof(stableKey));
        }

        internal void BindCreatedLines(Action<bool> createdLines)
        {
            m_CreatedLines = createdLines ?? throw new ArgumentNullException(nameof(createdLines));
        }

        protected override void OnUpdate()
        {
            bool hasCreatedLines = CollectUpdatedLines();
            CollectPathEvents();
            CollectDeletedLines();
            m_CreatedLines?.Invoke(hasCreatedLines);
            if ((++m_RoadDeletePruneTick & 255u) == 0u)
                PruneRoadDeletes();
        }

        internal void DrainChanges(List<LineChangeCandidate> output)
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

        internal void ResetPending()
        {
            m_PendingChanges.Clear();
            m_PendingIndexes.Clear();
            m_AcknowledgedRoadDeletes.Clear();
            m_RoadDeletePruneBuffer.Clear();
            m_RoadDeletePruneTick = 0;
        }

        internal void AcknowledgeRoadDeleted(Entity line)
        {
            if (line != Entity.Null)
                m_AcknowledgedRoadDeletes.Add(line);
        }

        internal void PruneRoadDeletes()
        {
            m_RoadDeletePruneBuffer.Clear();
            foreach (Entity line in m_AcknowledgedRoadDeletes)
            {
                if (!EntityManager.Exists(line))
                    m_RoadDeletePruneBuffer.Add(line);
            }
            for (int i = 0; i < m_RoadDeletePruneBuffer.Count; i++)
                m_AcknowledgedRoadDeletes.Remove(m_RoadDeletePruneBuffer[i]);
            m_RoadDeletePruneBuffer.Clear();
        }

        protected override void OnDestroy()
        {
            ResetPending();
            base.OnDestroy();
        }

        private bool CollectUpdatedLines()
        {
            if (m_UpdatedLineQuery.IsEmptyIgnoreFilter)
                return false;

            m_UpdatedLineQuery.CompleteDependency();
            NativeArray<Entity> lines = m_UpdatedLineQuery.ToEntityArray(Allocator.Temp);
            bool hasCreatedLines = false;
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (EntityManager.HasComponent<Created>(line))
                        hasCreatedLines = true;
                    AddChange(line, LineChangeKind.Layout);
                }
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }

            return hasCreatedLines;
        }

        private void CollectPathEvents()
        {
            if (m_PathUpdatedQuery.IsEmptyIgnoreFilter)
                return;

            m_PathUpdatedQuery.CompleteDependency();
            NativeArray<Entity> events = m_PathUpdatedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    PathUpdated pathUpdated = EntityManager.GetComponentData<PathUpdated>(events[i]);
                    Entity segment = pathUpdated.m_Owner;
                    if (segment == Entity.Null
                        || !EntityManager.Exists(segment)
                        || !EntityManager.HasComponent<Segment>(segment)
                        || !EntityManager.HasComponent<Owner>(segment))
                    {
                        continue;
                    }

                    Entity line = EntityManager.GetComponentData<Owner>(segment).m_Owner;
                    if (!IsLiveTransportLine(line))
                        continue;

                    AddChange(line, LineChangeKind.Path);
                }
            }
            finally
            {
                if (events.IsCreated)
                    events.Dispose();
            }
        }

        private void CollectDeletedLines()
        {
            if (m_StableKey == null)
                return;

            if (m_DeletedLineQuery.IsEmptyIgnoreFilter)
                return;

            m_DeletedLineQuery.CompleteDependency();

            NativeArray<Entity> lines = m_DeletedLineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    TransitMode mode = TransportModeResolver.Resolve(EntityManager, line);
                    LifecycleKind lifecycle = TransportModeProfile.GetProfile(mode).Lifecycle;
                    if (lifecycle == LifecycleKind.Road && m_AcknowledgedRoadDeletes.Contains(line))
                        continue;
                    if (lifecycle != LifecycleKind.Rail && lifecycle != LifecycleKind.Road)
                        continue;

                    LineKey lineKey = m_StableKey(line);
                    AddDeleted(new TrackLineDeletedFact(line, lineKey, mode));
                }
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
        }

        private bool IsLiveTransportLine(Entity line)
        {
            return line != Entity.Null
                && EntityManager.Exists(line)
                && EntityManager.HasComponent<TransportLine>(line)
                && !EntityManager.HasComponent<Deleted>(line)
                && !EntityManager.HasComponent<Temp>(line);
        }

        private void AddChange(Entity line, LineChangeKind kind)
        {
            if (line == Entity.Null)
                return;

            if (m_PendingIndexes.TryGetValue(line, out int index))
            {
                LineChangeCandidate previous = m_PendingChanges[index];
                if (previous.IsDeleted)
                    return;

                m_PendingChanges[index] = new LineChangeCandidate(
                    line,
                    previous.Kind | kind);
                return;
            }

            m_PendingIndexes.Add(line, m_PendingChanges.Count);
            m_PendingChanges.Add(new LineChangeCandidate(line, kind));
        }

        private void AddDeleted(TrackLineDeletedFact fact)
        {
            if (fact.Line == Entity.Null)
                return;

            if (m_PendingIndexes.TryGetValue(fact.Line, out int index))
            {
                m_PendingChanges[index] = new LineChangeCandidate(fact);
                return;
            }

            m_PendingIndexes.Add(fact.Line, m_PendingChanges.Count);
            m_PendingChanges.Add(new LineChangeCandidate(fact));
        }
    }
}
