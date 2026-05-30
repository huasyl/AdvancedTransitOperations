using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed partial class TrackModelService
    {
        internal void InvalidateLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            if (!m_Coordinator.Invalidate(line, out LineTrackChain existingChain))
                existingChain = null;

            m_LineTrackChainFrameSnapshots.Remove(line);
            m_LineWaypointIndexLookups.Remove(line);
            if (existingChain != null)
                RemoveDevSightLaneIndexForChain(existingChain);

            InvalidateStaticCachesForLine(line);
        }

        internal void InvalidateAll()
        {
            m_Coordinator.InvalidateAll();
            m_LineTrackChainFrameSnapshots.Clear();
            m_LineWaypointIndexLookups.Clear();
            m_DevSightLaneIndex.Clear();
            InvalidateAllStaticCaches();
        }

        private void InvalidateAllStaticCaches()
        {
            m_LocalBypassSceneStaticSnapshots.Clear();
            m_GlobalSharedTrunkSnapshots.Clear();
            m_ProtectedIntervalPairMetricsSnapshots.Clear();
            m_TrackModelSequenceLogCache.Clear();
            m_LineOrderedFallbackCaseLogCache.Clear();
            m_LineOrderedFallbackCaseLastLogFrame.Clear();
            m_TrackModelTurnbackBuildLogCache.Clear();
        }

        private void InvalidateStaticCachesForLine(Entity line)
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

            m_TrackModelSequenceLogCache.Remove(line);
            m_LineOrderedFallbackCaseLogCache.Remove(line);
            m_LineOrderedFallbackCaseLastLogFrame.Remove(line);
            m_TrackModelTurnbackBuildLogCache.Remove(line);
        }
    }
}
