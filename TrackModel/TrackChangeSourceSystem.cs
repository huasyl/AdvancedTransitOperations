using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
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
        internal readonly TrackLineDeletedFact DeletedFact;

        internal TrackChangeCandidate(Entity line, TrackChangeKind kind)
        {
            Line = line;
            Kind = kind;
            DeletedFact = default;
        }

        internal TrackChangeCandidate(TrackLineDeletedFact deletedFact)
        {
            Line = deletedFact.Line;
            Kind = TrackChangeKind.Deleted;
            DeletedFact = deletedFact;
        }

        internal bool IsDeleted => (Kind & TrackChangeKind.Deleted) != 0;
        internal bool LayoutChanged => (Kind & TrackChangeKind.Layout) != 0;
    }

    internal sealed partial class TrackChangeSourceSystem : GameSystemBase
    {
        private readonly List<TrackChangeCandidate> m_PendingChanges =
            new List<TrackChangeCandidate>(128);
        private readonly Dictionary<Entity, int> m_PendingIndexes =
            new Dictionary<Entity, int>(128);
        private EntityQuery m_UpdatedLineQuery;
        private EntityQuery m_PathUpdatedQuery;
        private EntityQuery m_DeletedLineQuery;
        private Func<Entity, LineKey> m_StableKey;

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

        protected override void OnUpdate()
        {
            CollectUpdatedLines();
            CollectPathEvents();
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

        internal void ResetPending()
        {
            m_PendingChanges.Clear();
            m_PendingIndexes.Clear();
        }

        protected override void OnDestroy()
        {
            ResetPending();
            base.OnDestroy();
        }

        private void CollectUpdatedLines()
        {
            if (m_UpdatedLineQuery.IsEmptyIgnoreFilter)
                return;

            m_UpdatedLineQuery.CompleteDependency();
            NativeArray<Entity> lines = m_UpdatedLineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                    AddChange(lines[i], TrackChangeKind.Layout);
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
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

                    AddChange(line, TrackChangeKind.Path);
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
                    if (TransportModeProfile.GetProfile(mode).Lifecycle != LifecycleKind.Rail)
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

        private void AddChange(Entity line, TrackChangeKind kind)
        {
            if (line == Entity.Null)
                return;

            if (m_PendingIndexes.TryGetValue(line, out int index))
            {
                TrackChangeCandidate previous = m_PendingChanges[index];
                if (previous.IsDeleted)
                    return;

                m_PendingChanges[index] = new TrackChangeCandidate(
                    line,
                    previous.Kind | kind);
                return;
            }

            m_PendingIndexes.Add(line, m_PendingChanges.Count);
            m_PendingChanges.Add(new TrackChangeCandidate(line, kind));
        }

        private void AddDeleted(TrackLineDeletedFact fact)
        {
            if (fact.Line == Entity.Null)
                return;

            if (m_PendingIndexes.TryGetValue(fact.Line, out int index))
            {
                m_PendingChanges[index] = new TrackChangeCandidate(fact);
                return;
            }

            m_PendingIndexes.Add(fact.Line, m_PendingChanges.Count);
            m_PendingChanges.Add(new TrackChangeCandidate(fact));
        }
    }
}
