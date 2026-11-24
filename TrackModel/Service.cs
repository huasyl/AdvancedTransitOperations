using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackModelService
    {
        private const int MaxPublishedChanges = 4096;
        private readonly TrackSupport m_Support;
        private readonly TrackState m_State;
        private readonly TrackBuild m_Build;
        private readonly TrackProfile m_Profile;
        private readonly TramStopIndex m_TramStops;
        private readonly TrackQuery m_Query;
        private readonly TrackDiag m_Diag;
        private readonly Dictionary<Entity, PublishedTraversalSnapshot> m_PublishedTraversals =
            new Dictionary<Entity, PublishedTraversalSnapshot>();
        private readonly List<Entity> m_PublishedTraversalOrder = new List<Entity>();
        private readonly List<PublishedTraversalSnapshot> m_PublishedChanges =
            new List<PublishedTraversalSnapshot>();
        private readonly System.Action<TrackLineDeletedFact> m_NotifyLineDeleted;
        private ulong m_PublishedTraversalVersion;

        internal TrackModelService(ITrackModelRuntimeContext runtime)
        {
            m_Support = new TrackSupport(runtime);
            m_State = new TrackState();
            m_Diag = new TrackDiag(m_Support);
            m_TramStops = new TramStopIndex(m_Support);
            m_Profile = new TrackProfile(m_Support, m_TramStops);
            m_Build = new TrackBuild(
                m_State,
                m_Support,
                m_Profile,
                m_Diag,
                runtime.NotifyLineTrackChainCandidate,
                runtime.NotifyLineTrackChainEstablished,
                PublishTraversal,
                InvalidateLine,
                ClearStaticCachesForLine);
            m_NotifyLineDeleted = runtime.NotifyLineDeleted;
            m_Query = new TrackQuery(m_State);
            m_Diag.Bind(m_Build);
        }

        internal void Dispose()
        {
        }

        internal ulong PublishedTraversalVersion => m_PublishedTraversalVersion;

        internal int CopyPublishedTraversalSnapshot(
            int cursor,
            int budget,
            List<PublishedTraversalSnapshot> output,
            out bool complete)
        {
            complete = false;
            if (output == null || budget <= 0)
                return 0;

            output.Clear();
            int start = math.clamp(cursor, 0, m_PublishedTraversalOrder.Count);
            int end = math.min(start + budget, m_PublishedTraversalOrder.Count);
            for (int i = start; i < end; i++)
            {
                if (m_PublishedTraversals.TryGetValue(m_PublishedTraversalOrder[i], out PublishedTraversalSnapshot snapshot))
                    output.Add(snapshot);
            }

            complete = end >= m_PublishedTraversalOrder.Count;
            return output.Count;
        }

        internal int CopyPublishedTraversalChanges(
            ulong afterVersion,
            int budget,
            List<PublishedTraversalSnapshot> output)
            => CopyPublishedTraversalChanges(afterVersion, budget, output, out _);

        internal int CopyPublishedTraversalChanges(
            ulong afterVersion,
            int budget,
            List<PublishedTraversalSnapshot> output,
            out bool historyGap)
        {
            historyGap = false;
            if (output == null || budget <= 0)
                return 0;
            output.Clear();
            if (m_PublishedTraversalVersion > afterVersion
                && (m_PublishedChanges.Count == 0
                    || m_PublishedChanges[0].PublishVersion > afterVersion + 1UL))
            {
                historyGap = true;
                return 0;
            }

            int low = 0;
            int high = m_PublishedChanges.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (m_PublishedChanges[middle].PublishVersion <= afterVersion)
                    low = middle + 1;
                else
                    high = middle;
            }
            for (int i = low; i < m_PublishedChanges.Count && output.Count < budget; i++)
                output.Add(m_PublishedChanges[i]);
            return output.Count;
        }

        internal void InvalidateLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_State.MarkDirty(line);
            if (m_State.RemoveLine(line, out LineTrackChain existingChain) && existingChain != null)
                m_Diag.RemoveDevSightChain(existingChain);

            PublishUnavailable(line);

            m_Profile.ClearLine(line);
            m_TramStops.RemoveLine(line);
        }

        internal void RequestTramStopAudit() => m_TramStops.RequestAudit();

        internal string[] RefreshTramStationNames(Entity line) => m_TramStops.RefreshNames(line);

        internal TramStationGroup[] ReadTramStationGroups(Entity line, string stationId, string[] affected = null)
            => m_TramStops.ReadGroups(line, stationId, affected);

        internal void TickTramStopIndex()
        {
            m_TramStops.Tick();
            var lines = new List<Entity>();
            m_TramStops.DrainDirtyLines(2, lines);
            for (int i = 0; i < lines.Count; i++)
                m_Build.RebuildTraversal(lines[i]);
        }

        internal void MarkLineDirty(Entity line)
        {
            m_State.MarkDirty(line);
        }

        internal void ConfirmLineChange(Entity line)
        {
            m_Build.ConfirmLineChange(line);
        }

        internal System.Func<Entity, Entity, bool> CreateTargetComparer()
        {
            TrackTargetComparer comparer = new TrackTargetComparer(m_Support.EntityManager);
            return comparer.AreEquivalent;
        }

        internal void ConfirmLineDeleted(TrackLineDeletedFact fact)
        {
            if (fact.Line == Entity.Null)
                return;

            InvalidateLine(fact.Line);
            m_NotifyLineDeleted?.Invoke(fact);
        }

        internal void InvalidateAll()
        {
            foreach (Entity line in m_PublishedTraversals.Keys.ToArray())
                PublishUnavailable(line);
            m_PublishedTraversals.Clear();
            m_PublishedTraversalOrder.Clear();
            m_PublishedChanges.Clear();
            m_State.ClearLines();
            m_Profile.ClearAll();
            m_TramStops.Clear();
            m_Diag.ClearAll();
        }

        internal bool TryGetChainForLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
            => m_Build.TryGetLineTrackChain(line, waypoints, out chain);

        internal bool TryChain(Entity line, out LineTrackChain chain) => m_Query.TryChain(line, out chain);
        internal bool TryProfile(Entity line, out LineTraversalProfile profile) => m_Query.TryProfile(line, out profile);

        private void PublishTraversal(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null || chain == null)
                return;
            chain.TraversalSignature = TraversalSignature(chain);
            PublishedTraversalSnapshot snapshot = new PublishedTraversalSnapshot
            {
                Line = line,
                PublishVersion = ++m_PublishedTraversalVersion,
                ChainSignature = chain.Signature,
                TraversalSignature = chain.TraversalSignature,
                ChainComplete = chain.ChainComplete,
                HasPhysicalTurnback = chain.TurnbackBoundaries.Count > 0,
                Available = chain.ChainComplete
                    && chain.TrackAtoms != null
                    && chain.TrackAtoms.Count > 0,
                Events = chain.TraversalProfile?.Events?.ToArray() ?? System.Array.Empty<TraversalEvent>(),
                RunChartTurnbackRegions = chain.RunChartTurnbackRegions?.ToArray()
                    ?? System.Array.Empty<RunChartTurnbackRegion>()
            };
            if (!m_PublishedTraversals.ContainsKey(line))
            {
                m_PublishedTraversalOrder.Add(line);
                SortPublishedTraversalOrder();
            }
            m_PublishedTraversals[line] = snapshot;
            AddPublishedChange(snapshot);
        }

        private void PublishUnavailable(Entity line)
        {
            if (line == Entity.Null || !m_PublishedTraversals.ContainsKey(line))
                return;
            PublishedTraversalSnapshot snapshot = new PublishedTraversalSnapshot
            {
                Line = line,
                PublishVersion = ++m_PublishedTraversalVersion,
                Available = false
            };
            m_PublishedTraversals.Remove(line);
            m_PublishedTraversalOrder.Remove(line);
            AddPublishedChange(snapshot);
        }

        private void SortPublishedTraversalOrder()
        {
            m_PublishedTraversalOrder.Sort((left, right) =>
            {
                int result = left.Index.CompareTo(right.Index);
                return result != 0 ? result : left.Version.CompareTo(right.Version);
            });
        }

        private void AddPublishedChange(PublishedTraversalSnapshot snapshot)
        {
            m_PublishedChanges.Add(snapshot);
            if (m_PublishedChanges.Count > MaxPublishedChanges)
                m_PublishedChanges.RemoveAt(0);
        }

        private ulong TraversalSignature(LineTrackChain chain)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (int)TransportModeResolver.Resolve(m_Support.EntityManager, chain.LineEntity));
            hash = Mix(hash, chain.Signature);
            hash = Mix(hash, chain.ChainComplete ? 1 : 0);
            hash = Mix(hash, chain.TurnbackBoundaries.Count > 0 ? 1 : 0);
            for (int i = 0; i < chain.TrackAtoms.Count; i++)
            {
                TrackAtom atom = chain.TrackAtoms[i];
                hash = Mix(hash, atom.Key.PhysicalLaneKey.Index);
                hash = Mix(hash, atom.TargetDelta.x);
                hash = Mix(hash, atom.TargetDelta.y);
            }
            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent item = chain.TraversalProfile.Events[i];
                hash = Mix(hash, (int)item.Kind);
                hash = Mix(hash, item.Building.Index);
                hash = Mix(hash, item.WaypointIndex);
                hash = Mix(hash, item.PassIndex);
                hash = Mix(hash, item.StartAtomIndex);
                hash = Mix(hash, item.EndAtomIndexExclusive);
                string stationId = item.StationId ?? string.Empty;
                for (int characterIndex = 0; characterIndex < stationId.Length; characterIndex++)
                    hash = Mix(hash, stationId[characterIndex]);
            }
            for (int i = 0; i < chain.RunChartTurnbackRegions.Count; i++)
            {
                RunChartTurnbackRegion region = chain.RunChartTurnbackRegions[i];
                hash = Mix(hash, region.BoundaryAtomIndex);
                hash = Mix(hash, region.StartAtomIndex);
                hash = Mix(hash, region.EndAtomIndexExclusive);
            }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                return (hash ^ value) * 1099511628211UL;
            }
        }

        private static ulong Mix(ulong hash, int value)
        {
            return Mix(hash, unchecked((ulong)(uint)value));
        }

        private static ulong Mix(ulong hash, float value)
        {
            return Mix(hash, unchecked((ulong)(uint)math.asint(value)));
        }

        internal void ClearAllStaticCaches()
        {
            m_Profile.ClearAll();
        }

        internal void ClearStaticCachesForLine(Entity line)
        {
            m_Profile.ClearLine(line);
        }

        public string BuildDevSightTooltipSummary(Entity laneEntity) => m_Diag.BuildDevSightTooltipSummary(laneEntity);
        internal void LogLineTrackChainDiagnostics(Entity line) => m_Diag.LogLineTrackChainDiagnostics(line);

        public void DumpTramTurnbacks()
        {
            if (!RtLog.DebugToolsEnabled)
                return;

            NativeArray<Entity> allLines = m_Support.GetLineEntities(Allocator.Temp);
            try
            {
                List<Entity> targetLines = CollectTramDumpLines(allLines);
                StringBuilder sb = new StringBuilder(8192);
                sb.Append("[TrackModelTramDump]").Append(" lines=").Append(targetLines.Count).AppendLine();
                StringBuilder lineSb = new StringBuilder(2048);
                for (int i = 0; i < targetLines.Count; i++)
                {
                    Entity line = targetLines[i];
                    if (!m_Query.TryChain(line, out LineTrackChain chain))
                        continue;

                    lineSb.Clear();
                    m_Profile.AppendRunChartTramDump(lineSb, chain, line);
                    if (lineSb.Length == 0)
                        continue;

                    sb.Append("--- line ---").AppendLine();
                    sb.Append("line=").Append(line.Index)
                      .Append(" label=").Append(m_Diag.ResolveTrackModelLineLabel(line, includeEntityFallback: true))
                      .AppendLine();
                    sb.Append(lineSb.ToString());
                }

                m_Support.Log.Info(sb.ToString().TrimEnd());
            }
            finally
            {
                allLines.Dispose();
            }
        }

        private List<Entity> CollectTramDumpLines(NativeArray<Entity> allLines)
        {
            EntityManager entityManager = m_Support.EntityManager;
            HashSet<Entity> lines = new HashSet<Entity>();
            foreach (KeyValuePair<string, AppliedLine> entry in m_Support.AppliedLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line != Entity.Null && entityManager.Exists(line))
                    lines.Add(line);
            }

            if (lines.Count == 0)
            {
                for (int i = 0; i < allLines.Length; i++)
                {
                    Entity line = allLines[i];
                    if (line != Entity.Null && entityManager.Exists(line))
                        lines.Add(line);
                }
            }

            List<Entity> ordered = new List<Entity>(lines);
            ordered.Sort((a, b) =>
            {
                int cmp = string.CompareOrdinal(
                    m_Diag.ResolveTrackModelLineLabel(a, includeEntityFallback: true),
                    m_Diag.ResolveTrackModelLineLabel(b, includeEntityFallback: true));
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });
            return ordered;
        }
    }
}
