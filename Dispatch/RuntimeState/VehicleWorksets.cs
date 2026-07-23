using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleWorksets : IDisposable
    {
        // 仅派生桶：不保存 Entity 到状态或模式的第二份权威值。
        private readonly Dictionary<TransitMode, HashSet<Entity>> m_ModeBuckets = new Dictionary<TransitMode, HashSet<Entity>>();
        private readonly Dictionary<VehicleState, HashSet<Entity>> m_StateBuckets = new Dictionary<VehicleState, HashSet<Entity>>();

        public IReadOnlyCollection<Entity> Mode(TransitMode mode) => Bucket(m_ModeBuckets, mode);
        public IReadOnlyCollection<Entity> State(VehicleState state) => Bucket(m_StateBuckets, state);
        public bool ContainsMode(Entity vehicle, TransitMode mode) => Bucket(m_ModeBuckets, mode).Contains(vehicle);
        public bool ContainsState(Entity vehicle, VehicleState state) => Bucket(m_StateBuckets, state).Contains(vehicle);
        public bool HasOnlyMode(Entity vehicle, TransitMode mode) => HasOnly(m_ModeBuckets, vehicle, mode);
        public bool HasOnlyState(Entity vehicle, VehicleState state) => HasOnly(m_StateBuckets, vehicle, state);
        public bool MatchesModeKeys(HashSet<Entity> keys) => MatchesKeys(m_ModeBuckets, keys);
        public bool MatchesStateKeys(HashSet<Entity> keys) => MatchesKeys(m_StateBuckets, keys);
        public void AddMode(Entity vehicle, TransitMode mode) => Bucket(m_ModeBuckets, mode).Add(vehicle);
        public void RemoveMode(Entity vehicle, TransitMode mode) => Bucket(m_ModeBuckets, mode).Remove(vehicle);
        public void RemoveMode(Entity vehicle)
        {
            foreach (KeyValuePair<TransitMode, HashSet<Entity>> entry in m_ModeBuckets)
                entry.Value.Remove(vehicle);
        }
        public void AddState(Entity vehicle, VehicleState state) => Bucket(m_StateBuckets, state).Add(vehicle);
        public void RemoveState(Entity vehicle, VehicleState state) => Bucket(m_StateBuckets, state).Remove(vehicle);
        // 仅整体重置清桶；普通帧始终由 Registry 增量维护。
        public void ResetCity() { m_ModeBuckets.Clear(); m_StateBuckets.Clear(); }
        // 销毁时清全部派生桶，不触及 VehicleStateStore。
        public void Dispose() => ResetCity();
        private static bool HasOnly<TKey>(Dictionary<TKey, HashSet<Entity>> buckets, Entity vehicle, TKey expected)
        {
            int count = 0;
            foreach (KeyValuePair<TKey, HashSet<Entity>> entry in buckets)
            {
                if (!entry.Value.Contains(vehicle)) continue;
                count++;
                if (!EqualityComparer<TKey>.Default.Equals(entry.Key, expected)) return false;
            }
            return count == 1;
        }

        private static bool MatchesKeys<TKey>(Dictionary<TKey, HashSet<Entity>> buckets, HashSet<Entity> keys)
        {
            var seen = new HashSet<Entity>();
            foreach (KeyValuePair<TKey, HashSet<Entity>> entry in buckets)
            {
                foreach (Entity vehicle in entry.Value)
                {
                    if (!keys.Contains(vehicle) || !seen.Add(vehicle)) return false;
                }
            }
            return seen.SetEquals(keys);
        }

        private static HashSet<Entity> Bucket<TKey>(Dictionary<TKey, HashSet<Entity>> buckets, TKey key) { if (!buckets.TryGetValue(key, out HashSet<Entity> bucket)) { bucket = new HashSet<Entity>(); buckets.Add(key, bucket); } return bucket; }
    }
}
