using System.Collections.Generic;
using RapidTransitMod.Bypass;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class SceneCache
    {
        private readonly Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot> m_LocalBypassSceneStaticSnapshots = new Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot>();
        private readonly Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> m_GlobalSharedTrunkSnapshots = new Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot>();
        private readonly Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> m_ProtectedIntervalPairMetricsSnapshots = new Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot>();

        internal bool TryGetStaticSceneSnapshot(LocalBypassSceneStaticKey key, out LocalBypassSceneStaticSnapshot snapshot)
            => m_LocalBypassSceneStaticSnapshots.TryGetValue(key, out snapshot);

        internal void PutStaticSceneSnapshot(LocalBypassSceneStaticKey key, LocalBypassSceneStaticSnapshot snapshot)
        {
            m_LocalBypassSceneStaticSnapshots[key] = snapshot;
        }

        internal Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> GlobalSharedTrunkSnapshots => m_GlobalSharedTrunkSnapshots;
        internal Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> ProtectedIntervalPairMetricsSnapshots => m_ProtectedIntervalPairMetricsSnapshots;

        internal void ClearAll()
        {
            m_LocalBypassSceneStaticSnapshots.Clear();
            m_GlobalSharedTrunkSnapshots.Clear();
            m_ProtectedIntervalPairMetricsSnapshots.Clear();
        }

        internal void ClearLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<LocalBypassSceneStaticKey> localSceneKeysToRemove = null;
            foreach (KeyValuePair<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot> entry in m_LocalBypassSceneStaticSnapshots)
            {
                if (entry.Key.Line != line)
                    continue;

                localSceneKeysToRemove ??= new List<LocalBypassSceneStaticKey>();
                localSceneKeysToRemove.Add(entry.Key);
            }

            if (localSceneKeysToRemove != null)
            {
                for (int i = 0; i < localSceneKeysToRemove.Count; i++)
                    m_LocalBypassSceneStaticSnapshots.Remove(localSceneKeysToRemove[i]);
            }

            List<GlobalSharedTrunkCacheKey> trunkKeysToRemove = null;
            foreach (KeyValuePair<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> entry in m_GlobalSharedTrunkSnapshots)
            {
                GlobalSharedTrunkCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                trunkKeysToRemove ??= new List<GlobalSharedTrunkCacheKey>();
                trunkKeysToRemove.Add(key);
            }

            if (trunkKeysToRemove != null)
            {
                for (int i = 0; i < trunkKeysToRemove.Count; i++)
                    m_GlobalSharedTrunkSnapshots.Remove(trunkKeysToRemove[i]);
            }

            List<ProtectedIntervalPairMetricsCacheKey> pairMetricKeysToRemove = null;
            foreach (KeyValuePair<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> entry in m_ProtectedIntervalPairMetricsSnapshots)
            {
                ProtectedIntervalPairMetricsCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                pairMetricKeysToRemove ??= new List<ProtectedIntervalPairMetricsCacheKey>();
                pairMetricKeysToRemove.Add(key);
            }

            if (pairMetricKeysToRemove != null)
            {
                for (int i = 0; i < pairMetricKeysToRemove.Count; i++)
                    m_ProtectedIntervalPairMetricsSnapshots.Remove(pairMetricKeysToRemove[i]);
            }
        }
    }
}
