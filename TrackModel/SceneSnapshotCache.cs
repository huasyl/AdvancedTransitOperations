using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed partial class TrackModelService
    {
        private readonly Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot> m_LocalBypassSceneStaticSnapshots = new Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot>();
        private readonly Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> m_GlobalSharedTrunkSnapshots = new Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot>();
        private readonly Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> m_ProtectedIntervalPairMetricsSnapshots = new Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot>();

        private bool TryGetStaticSceneSnapshot(LocalBypassSceneStaticKey key, out LocalBypassSceneStaticSnapshot snapshot)
        {
            return m_LocalBypassSceneStaticSnapshots.TryGetValue(key, out snapshot);
        }

        private void PutStaticSceneSnapshot(LocalBypassSceneStaticKey key, LocalBypassSceneStaticSnapshot snapshot)
        {
            m_LocalBypassSceneStaticSnapshots[key] = snapshot;
        }
    }
}
