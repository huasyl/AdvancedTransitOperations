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
        private static HashSet<Entity> Bucket<TKey>(Dictionary<TKey, HashSet<Entity>> buckets, TKey key) { if (!buckets.TryGetValue(key, out HashSet<Entity> bucket)) { bucket = new HashSet<Entity>(); buckets.Add(key, bucket); } return bucket; }
    }
}
