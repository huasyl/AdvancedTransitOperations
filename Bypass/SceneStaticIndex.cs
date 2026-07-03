using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct SceneStaticIndexKey : IEquatable<SceneStaticIndexKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity CurrentBypassBuilding;
        public readonly int LocalProtectedIntervalIndex;

        public SceneStaticIndexKey(Entity localLine, Entity currentBypassBuilding, int localProtectedIntervalIndex)
        {
            LocalLine = localLine;
            CurrentBypassBuilding = currentBypassBuilding;
            LocalProtectedIntervalIndex = localProtectedIntervalIndex;
        }

        public bool Equals(SceneStaticIndexKey other)
        {
            return LocalLine == other.LocalLine
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && LocalProtectedIntervalIndex == other.LocalProtectedIntervalIndex;
        }

        public override bool Equals(object obj) => obj is SceneStaticIndexKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalLine.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ LocalProtectedIntervalIndex;
                return hash;
            }
        }
    }

    internal sealed class SceneStaticIndexEntry
    {
        public uint SharedTrackVersion;
        public ulong LocalChainSignature;
        public uint LocalSceneVersion;
        public BypassExecutionMode ExecutionMode;
        public readonly List<SceneExpressRelation> ExpressRelations = new List<SceneExpressRelation>();
    }

    internal interface ISceneStaticIndexBuilder
    {
        bool TryBuildRelation(
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            Entity expressLine,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            out SceneExpressRelation relation);
    }

    internal sealed class SceneStaticIndex
    {
        private readonly IBypassAdmissionRuntimeContext m_Runtime;
        private readonly ISceneStaticIndexBuilder m_Builder;
        private readonly Dictionary<SceneStaticIndexKey, SceneStaticIndexEntry> m_Entries = new Dictionary<SceneStaticIndexKey, SceneStaticIndexEntry>();
        private bool m_Dirty = true;

        internal SceneStaticIndex(
            IBypassAdmissionRuntimeContext runtime,
            ISceneStaticIndexBuilder builder)
        {
            m_Runtime = runtime;
            m_Builder = builder;
        }

        internal void Clear()
        {
            m_Entries.Clear();
            m_Dirty = true;
        }

        internal void MarkDirty() => m_Dirty = true;

        internal void WarmAll()
        {
            RebuildAll();
        }

        internal bool TryGetEntry(
            LineTrackChain localChain,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            out SceneStaticIndexEntry entry)
        {
            entry = null;
            if (localChain == null)
                return false;

            if (m_Dirty)
                RebuildAll();

            var key = new SceneStaticIndexKey(
                localChain.LineEntity,
                currentBypassBuilding,
                protectedIntervalIndex);
            if (!m_Entries.TryGetValue(key, out entry))
                return TryBuildEntryForCurrentScene(localChain, currentBypassBuilding, protectedIntervalIndex, out entry);

            if (entry.SharedTrackVersion != m_Runtime.TrackModel.SharedIndexVersion
                || entry.LocalChainSignature != localChain.Signature
                || entry.LocalSceneVersion != localChain.LocalBypassWaypointScenesVersion)
            {
                MarkDirty();
                RebuildAll();
                return m_Entries.TryGetValue(key, out entry);
            }

            if (!RelationsCurrent(entry))
            {
                MarkDirty();
                RebuildAll();
                return m_Entries.TryGetValue(key, out entry);
            }

            return true;
        }

        private bool RelationsCurrent(SceneStaticIndexEntry entry)
        {
            if (entry == null || entry.ExpressRelations.Count == 0)
                return true;

            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            for (int i = 0; i < entry.ExpressRelations.Count; i++)
            {
                SceneExpressRelation relation = entry.ExpressRelations[i];
                if (relation.ExpressLine == Entity.Null
                    || relation.ExpressChain == null
                    || !m_Runtime.EntityManager.Exists(relation.ExpressLine)
                    || !routeWaypointBuffers.TryGetBuffer(relation.ExpressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !m_Runtime.TrackModel.TryGetChainForLine(relation.ExpressLine, expressWaypoints, out LineTrackChain currentExpressChain)
                    || currentExpressChain.Signature != relation.ExpressChain.Signature)
                {
                    return false;
                }
            }

            return true;
        }

        private void RebuildAll()
        {
            m_Entries.Clear();
            m_Dirty = false;
            m_Runtime.TrackModel.EnsureSharedTrackIndexCurrent();
            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            foreach (KeyValuePair<string, AppliedLine> entry in m_Runtime.AppliedLines)
            {
                Entity localLine = entry.Value.LineEntity;
                if (localLine == Entity.Null
                    || !m_Runtime.EntityManager.Exists(localLine)
                    || !m_Runtime.EntityManager.HasComponent<TransportLine>(localLine)
                    || !m_Runtime.IsAppliedLocal(localLine)
                    || !routeWaypointBuffers.TryGetBuffer(localLine, out DynamicBuffer<RouteWaypoint> localWaypoints)
                    || !m_Runtime.TrackModel.TryGetChainForLine(localLine, localWaypoints, out LineTrackChain localChain))
                {
                    continue;
                }

                m_Runtime.TrackModel.EnsureBypassPipelineReady(localChain);
                if (localChain.LocalBypassWaypointScenes == null
                    || localChain.LocalBypassWaypointScenes.Length != localWaypoints.Length
                    || localChain.LocalBypassWaypointScenesVersion == 0)
                {
                    m_Runtime.TrackModel.TryGetLocalSceneSnapshot(localLine, localWaypoints, 0, out _, out _);
                }

                if (localChain.LocalBypassWaypointScenes == null || localChain.LocalBypassWaypointScenes.Length == 0)
                    continue;

                var seenScenes = new HashSet<SceneKey>();
                var lineEntries = new List<SceneStaticIndexEntry>();
                for (int waypointIndex = 0; waypointIndex < localChain.LocalBypassWaypointScenes.Length; waypointIndex++)
                {
                    LocalBypassWaypointSceneBinding binding = localChain.LocalBypassWaypointScenes[waypointIndex];
                    if (!binding.Available || !seenScenes.Add(binding.SceneKey))
                        continue;

                    SceneStaticIndexEntry sceneEntry = BuildScene(localChain, binding, routeWaypointBuffers);
                    lineEntries.Add(sceneEntry);
                }

                BypassExecutionMode executionMode = ResolveExecutionMode(lineEntries);
                for (int entryIndex = 0; entryIndex < lineEntries.Count; entryIndex++)
                    lineEntries[entryIndex].ExecutionMode = executionMode;
            }
        }

        private SceneStaticIndexEntry BuildScene(
            LineTrackChain localChain,
            LocalBypassWaypointSceneBinding binding,
            BufferLookup<RouteWaypoint> routeWaypointBuffers)
        {
            var key = new SceneStaticIndexKey(
                localChain.LineEntity,
                binding.CurrentBypassBuilding,
                binding.ProtectedIntervalIndex);
            var entry = new SceneStaticIndexEntry
            {
                SharedTrackVersion = m_Runtime.TrackModel.SharedIndexVersion,
                LocalChainSignature = localChain.Signature,
                LocalSceneVersion = localChain.LocalBypassWaypointScenesVersion,
                ExecutionMode = BypassExecutionMode.ComplexLineModel
            };

            if (localChain.SharedRunsByOtherLine != null)
            {
                var expressLines = new List<Entity>(localChain.SharedRunsByOtherLine.Keys);
                foreach (Entity expressLine in expressLines)
                {
                    if (expressLine == Entity.Null
                        || expressLine == localChain.LineEntity
                        || !m_Runtime.EntityManager.Exists(expressLine)
                        || !m_Runtime.EntityManager.HasComponent<TransportLine>(expressLine)
                        || !m_Runtime.IsAppliedExpress(expressLine)
                        || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                    {
                        continue;
                    }

                    if (!m_Builder.TryBuildRelation(
                        localChain,
                        binding.ProtectedIntervalIndex,
                        binding.ProtectedInterval,
                        binding.CurrentBypassBuilding,
                        expressLine,
                        expressWaypoints,
                        out SceneExpressRelation relation))
                    {
                        continue;
                    }

                    if (relation.ExpressLine != Entity.Null)
                        entry.ExpressRelations.Add(relation);
                }
            }

            m_Entries[key] = entry;
            return entry;
        }

        internal bool TryGetLineExecutionMode(LineTrackChain localChain, out BypassExecutionMode mode)
        {
            mode = BypassExecutionMode.ComplexLineModel;
            if (localChain == null)
                return false;

            foreach (KeyValuePair<SceneStaticIndexKey, SceneStaticIndexEntry> entry in m_Entries)
            {
                if (entry.Key.LocalLine != localChain.LineEntity
                    || entry.Value.LocalChainSignature != localChain.Signature
                    || entry.Value.LocalSceneVersion != localChain.LocalBypassWaypointScenesVersion)
                {
                    continue;
                }

                mode = entry.Value.ExecutionMode;
                return true;
            }

            return false;
        }

        private bool TryBuildEntryForCurrentScene(
            LineTrackChain localChain,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            out SceneStaticIndexEntry entry)
        {
            entry = null;
            if (localChain?.LocalBypassWaypointScenes == null)
                return false;

            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            for (int waypointIndex = 0; waypointIndex < localChain.LocalBypassWaypointScenes.Length; waypointIndex++)
            {
                LocalBypassWaypointSceneBinding binding = localChain.LocalBypassWaypointScenes[waypointIndex];
                if (!binding.Available
                    || binding.CurrentBypassBuilding != currentBypassBuilding
                    || binding.ProtectedIntervalIndex != protectedIntervalIndex)
                {
                    continue;
                }

                entry = BuildScene(localChain, binding, routeWaypointBuffers);
                return true;
            }

            return false;
        }

        private static BypassExecutionMode ResolveExecutionMode(List<SceneStaticIndexEntry> entries)
        {
            int sceneCount = entries != null ? entries.Count : 0;
            int maxExpressLinesPerScene = 0;
            int multiTrunkSceneCount = 0;
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    SceneStaticIndexEntry entry = entries[i];
                    int expressLineCount = entry.ExpressRelations.Count;
                    if (expressLineCount > maxExpressLinesPerScene)
                        maxExpressLinesPerScene = expressLineCount;

                    bool sceneHasMultiTrunk = false;
                    for (int relationIndex = 0; relationIndex < entry.ExpressRelations.Count; relationIndex++)
                    {
                        SceneExpressRelation relation = entry.ExpressRelations[relationIndex];
                        if (relation.TrunkCandidates != null && relation.TrunkCandidates.Segments.Count > 1)
                        {
                            sceneHasMultiTrunk = true;
                            break;
                        }
                    }

                    if (sceneHasMultiTrunk)
                        multiTrunkSceneCount++;
                }
            }

            return sceneCount <= 2
                && maxExpressLinesPerScene <= 1
                && multiTrunkSceneCount == 0
                    ? BypassExecutionMode.SimpleSceneScan
                    : BypassExecutionMode.ComplexLineModel;
        }
    }
}
