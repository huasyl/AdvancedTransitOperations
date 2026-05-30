using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal enum BypassEntryKind : byte
    {
        Scope = 1,
        Cadence = 2,
        Episode = 3,
    }

    internal struct BypassHoldCadenceSnapshot
    {
        public SceneKey SceneKey;
        public int WaypointIndex;
        public uint EvaluatedFrame;
        public uint ReevaluateAfterFrame;
        public bool ShouldHold;
        public bool CanClearAfterExit;
        public BypassConflictMode ConflictMode;
        public Entity Blocker;

        public BypassHoldCadenceSnapshot(
            SceneKey sceneKey,
            int waypointIndex,
            uint evaluatedFrame,
            uint reevaluateAfterFrame,
            bool shouldHold,
            bool canClearAfterExit,
            BypassConflictMode conflictMode,
            Entity blocker)
        {
            SceneKey = sceneKey;
            WaypointIndex = waypointIndex;
            EvaluatedFrame = evaluatedFrame;
            ReevaluateAfterFrame = reevaluateAfterFrame;
            ShouldHold = shouldHold;
            CanClearAfterExit = canClearAfterExit;
            ConflictMode = conflictMode;
            Blocker = blocker;
        }
    }

    internal readonly struct BypassControlScope
    {
        public readonly Entity Vehicle;
        public readonly VehicleSceneBinding SceneBinding;
        public readonly SceneDefinition Scene;

        public BypassControlScope(
            Entity vehicle,
            VehicleSceneBinding sceneBinding,
            SceneDefinition scene)
        {
            Vehicle = vehicle;
            SceneBinding = sceneBinding;
            Scene = scene;
        }

        public Entity Line => Scene.Line;
        public int WaypointIndex => SceneBinding.WaypointIndex;
        public Entity CurrentBypassBuilding => Scene.CurrentBypassBuilding;
        public Entity NextBypassBuilding => Scene.NextBypassBuilding;
        public SceneKey SceneKey => Scene.Key;
    }

    internal readonly struct BypassControlScopeCacheEntry
    {
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly BypassControlScope Scope;

        public BypassControlScopeCacheEntry(
            Entity line,
            int waypointIndex,
            BypassControlScope scope)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            Scope = scope;
        }
    }

    internal sealed class BypassStateStore : IDisposable
    {
        private NativeHashMap<Entity, Entity> m_Blockers;
        private readonly Dictionary<Entity, BypassControlScopeCacheEntry> m_Scope = new Dictionary<Entity, BypassControlScopeCacheEntry>();
        private readonly Dictionary<Entity, BypassHoldCadenceSnapshot> m_Cadence = new Dictionary<Entity, BypassHoldCadenceSnapshot>();
        private readonly Dictionary<Entity, BypassConflictEpisode> m_Conflict = new Dictionary<Entity, BypassConflictEpisode>();

        public BypassStateStore()
        {
            m_Blockers = new NativeHashMap<Entity, Entity>(256, Allocator.Persistent);
        }

        public NativeHashMap<Entity, Entity> Blockers => m_Blockers;
        public Dictionary<Entity, BypassControlScopeCacheEntry> Scope => m_Scope;
        public Dictionary<Entity, BypassHoldCadenceSnapshot> Cadence => m_Cadence;
        public Dictionary<Entity, BypassConflictEpisode> Conflict => m_Conflict;

        public void Dispose()
        {
            if (m_Blockers.IsCreated)
                m_Blockers.Dispose();
        }

        public bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker)
        {
            blocker = Entity.Null;
            return vehicle != Entity.Null
                && m_Blockers.IsCreated
                && m_Blockers.TryGetValue(vehicle, out blocker);
        }

        public void SetBlocker(Entity vehicle, Entity blocker)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null || !m_Blockers.IsCreated)
                return;

            m_Blockers[vehicle] = blocker;
        }

        public void ClearBlocker(Entity vehicle)
        {
            if (vehicle != Entity.Null && m_Blockers.IsCreated)
                m_Blockers.Remove(vehicle);
        }

        public bool Get(Entity vehicle, out BypassControlScopeCacheEntry scope)
        {
            return m_Scope.TryGetValue(vehicle, out scope);
        }

        public bool Get(Entity vehicle, out BypassHoldCadenceSnapshot cadence)
        {
            return m_Cadence.TryGetValue(vehicle, out cadence);
        }

        public bool Get(Entity vehicle, out BypassConflictEpisode episode)
        {
            return m_Conflict.TryGetValue(vehicle, out episode);
        }

        public void Put(Entity vehicle, BypassControlScopeCacheEntry scope)
        {
            if (vehicle != Entity.Null)
                m_Scope[vehicle] = scope;
        }

        public void Put(Entity vehicle, BypassHoldCadenceSnapshot cadence)
        {
            if (vehicle != Entity.Null)
                m_Cadence[vehicle] = cadence;
        }

        public void Put(Entity vehicle, BypassConflictEpisode episode)
        {
            if (vehicle != Entity.Null)
                m_Conflict[vehicle] = episode;
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            ClearBlocker(vehicle);
            m_Scope.Remove(vehicle);
            m_Cadence.Remove(vehicle);
            m_Conflict.Remove(vehicle);
        }

        public void Remove(Entity vehicle, BypassEntryKind kind)
        {
            if (vehicle == Entity.Null)
                return;

            switch (kind)
            {
                case BypassEntryKind.Scope:
                    m_Scope.Remove(vehicle);
                    break;
                case BypassEntryKind.Cadence:
                    m_Cadence.Remove(vehicle);
                    break;
                case BypassEntryKind.Episode:
                    m_Conflict.Remove(vehicle);
                    break;
            }
        }

        public void Clear()
        {
            if (m_Blockers.IsCreated)
                m_Blockers.Clear();

            m_Scope.Clear();
            m_Cadence.Clear();
            m_Conflict.Clear();
        }
    }
}
