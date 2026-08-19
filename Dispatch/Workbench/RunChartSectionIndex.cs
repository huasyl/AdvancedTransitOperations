using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    // 纯工作台目录：只把已发布的 Stop/Pass 事实投影为按交通方式分开的有向站间边。
    internal sealed class RunChartSectionIndex
    {
        private const int SnapshotItemsPerTick = 8;
        private const int SourceItemsPerTick = 4;
        private const int FactItemsPerTick = 128;
        private const int EdgeItemsPerTick = 128;
        private const int NetworkItemsPerTick = 128;
        private const int SectionItemsPerTick = 16;
        private const int CoverageItemsPerTick = 32;
        private const int PublishItemsPerTick = 32;
        private const int MaxDirectoryStations = 8192;
        private const int MaxEdges = 32768;
        private const int MaxAttachments = 65536;
        private const int MaxSections = 65536;
        private const int MaxSectionEvents = 64;
        private const int MaxQueuedSections = 16384;
        private const int MaxCoverages = 32768;

        private readonly TrackModelService m_TrackModel;
        private readonly EntityManager m_Entities;
        private readonly Func<Entity, string> m_StationId;
        private readonly Func<Entity, string> m_StationName;
        private readonly Func<Entity, string> m_LineId;
        private readonly ModeState[] m_Modes = new ModeState[4];
        private Dictionary<Entity, LineSource> m_Sources = new Dictionary<Entity, LineSource>();
        private List<Entity> m_SourceOrder = new List<Entity>();
        private Dictionary<Entity, LineSource> m_LoadingSources = new Dictionary<Entity, LineSource>();
        private List<Entity> m_LoadingSourceOrder = new List<Entity>();
        private readonly List<PublishedTraversalSnapshot> m_SnapshotBatch =
            new List<PublishedTraversalSnapshot>();
        private readonly List<LineSource> m_BuildSources = new List<LineSource>();
        private readonly Dictionary<string, StationItem> m_BuildStations =
            new Dictionary<string, StationItem>(StringComparer.Ordinal);
        private readonly Dictionary<string, StationEdge> m_BuildEdges =
            new Dictionary<string, StationEdge>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_BuildNetworkParents =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<StationEdge>> m_BuildOutgoing =
            new Dictionary<string, List<StationEdge>>(StringComparer.Ordinal);
        private readonly SortedSet<string> m_BuildStartStations =
            new SortedSet<string>(StringComparer.Ordinal);
        private readonly Queue<SectionSeed> m_BuildSectionsQueue = new Queue<SectionSeed>();
        private readonly HashSet<string> m_BuildSeedKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Section> m_BuildSections =
            new Dictionary<string, Section>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Section>> m_BuildSectionsByStationPair =
            new Dictionary<string, List<Section>>(StringComparer.Ordinal);
        private int m_LoadIndex;
        private ulong m_LoadVersion;
        private ulong m_ConsumedVersion;
        private bool m_Started;
        private bool m_LoadingSnapshot;
        private bool m_Building;
        private bool m_BuildOverflow;
        private OverflowDiagnostic m_BuildDiagnostic = new OverflowDiagnostic();
        private TransitMode m_BuildMode;
        private ulong m_BuildGeneration;
        private int m_NextModeIndex = (int)TransitMode.Train;
        private int m_BuildSourceIndex;
        private int m_BuildPhaseIndex;
        private int m_BuildEventIndex;
        private int m_BuildAttachmentCount;
        private int m_BuildCoverageCount;
        private IEnumerator<string> m_BuildStartReader;
        private IEnumerator<KeyValuePair<string, StationItem>> m_BuildNetworkStations;
        private SectionSeed m_BuildActiveSeed;
        private IEnumerator<Section> m_BuildCoverageReader;
        private CoverageWork m_BuildCoverageWork;
        private Dictionary<string, Section> m_NextSections;
        private Dictionary<string, List<Section>> m_NextSectionsByStationPair;
        private Dictionary<string, StationItem> m_NextStations;
        private IEnumerator<KeyValuePair<string, Section>> m_PublishSections;
        private IEnumerator<KeyValuePair<string, List<Section>>> m_PublishStationPairs;
        private IEnumerator<KeyValuePair<string, StationItem>> m_PublishStations;
        private byte m_PublishStage;
        private BuildPhase m_BuildPhase;

        private enum BuildPhase : byte
        {
            None,
            Sources,
            Facts,
            Edges,
            Networks,
            Sections,
            Coverage,
            Publish
        }

        private enum OverflowReason : byte
        {
            None,
            DirectoryStationLimit,
            EdgeLimit,
            AttachmentLimit,
            SameStationAdjacent,
            QueueLimit,
            SectionLimit,
            SectionLengthLimit,
            SectionIdCollision,
            CoverageLimit
        }

        internal RunChartSectionIndex(
            TrackModelService trackModel,
            EntityManager entities,
            Func<Entity, string> stationId,
            Func<Entity, string> stationName,
            Func<Entity, string> lineId)
        {
            m_TrackModel = trackModel ?? throw new ArgumentNullException(nameof(trackModel));
            m_Entities = entities;
            m_StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
            m_StationName = stationName ?? throw new ArgumentNullException(nameof(stationName));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_Modes[(int)TransitMode.Train] = new ModeState(TransitMode.Train);
            m_Modes[(int)TransitMode.Subway] = new ModeState(TransitMode.Subway);
            m_Modes[(int)TransitMode.Tram] = new ModeState(TransitMode.Tram);
        }

        internal void Tick()
        {
            if (!m_Started || m_LoadingSnapshot)
            {
                if (!m_LoadingSnapshot)
                    BeginSnapshotLoad();
                TickSnapshotLoad();
                return;
            }

            m_SnapshotBatch.Clear();
            m_TrackModel.CopyPublishedTraversalChanges(
                m_ConsumedVersion,
                SnapshotItemsPerTick,
                m_SnapshotBatch,
                out bool historyGap);
            if (historyGap)
            {
                if (m_Building)
                    DiscardBuild();
                BeginSnapshotLoad();
                return;
            }

            for (int i = 0; i < m_SnapshotBatch.Count; i++)
            {
                ApplyChangedSnapshot(m_SnapshotBatch[i]);
                m_ConsumedVersion = Math.Max(m_ConsumedVersion, m_SnapshotBatch[i].PublishVersion);
            }

            if (m_Building && !BuildVersionMatches())
                DiscardBuild();
            if (m_ConsumedVersion != m_TrackModel.PublishedTraversalVersion)
                return;
            if (!m_Building)
                BeginNextBuild();
            if (!m_Building)
                return;

            switch (m_BuildPhase)
            {
                case BuildPhase.Sources:
                    if (!LoadBuildSourcesWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    if (m_BuildOverflow)
                    {
                        StopOverflow();
                        return;
                    }
                    m_BuildSourceIndex = 0;
                    m_BuildPhaseIndex = 0;
                    m_BuildEventIndex = 0;
                    m_BuildPhase = BuildPhase.Facts;
                    return;
                case BuildPhase.Facts:
                    if (!BuildFactsWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    BeginEdgeBuild();
                    return;
                case BuildPhase.Edges:
                    if (!BuildEdgesWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    BeginNetworkBuild();
                    return;
                case BuildPhase.Networks:
                    if (!BuildNetworksWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    BeginSectionBuild();
                    return;
                case BuildPhase.Sections:
                    if (!BuildSectionsWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    BeginCoverageBuild();
                    return;
                case BuildPhase.Coverage:
                    if (!BuildCoverageWork())
                    {
                        if (m_BuildOverflow)
                            StopOverflow();
                        return;
                    }
                    BeginPublish();
                    return;
                case BuildPhase.Publish:
                    PublishWork();
                    return;
            }
        }

        private void BeginSnapshotLoad()
        {
            m_LoadingSnapshot = true;
            m_LoadIndex = 0;
            m_LoadVersion = m_TrackModel.PublishedTraversalVersion;
            m_LoadingSources = new Dictionary<Entity, LineSource>();
            m_LoadingSourceOrder = new List<Entity>();
        }

        private void TickSnapshotLoad()
        {
            if (m_LoadVersion != m_TrackModel.PublishedTraversalVersion)
            {
                BeginSnapshotLoad();
                return;
            }

            m_SnapshotBatch.Clear();
            int copied = m_TrackModel.CopyPublishedTraversalSnapshot(
                m_LoadIndex,
                SnapshotItemsPerTick,
                m_SnapshotBatch,
                out bool complete);
            for (int i = 0; i < copied; i++)
                ApplySnapshot(m_SnapshotBatch[i], m_LoadingSources, m_LoadingSourceOrder);
            m_LoadIndex += copied;
            if (!complete)
                return;
            if (m_LoadVersion != m_TrackModel.PublishedTraversalVersion)
            {
                BeginSnapshotLoad();
                return;
            }

            bool firstLoad = !m_Started;
            for (int index = (int)TransitMode.Train; index <= (int)TransitMode.Tram; index++)
            {
                TransitMode mode = (TransitMode)index;
                if (firstLoad
                    || ModeSourceSignature(m_Sources, m_SourceOrder, mode)
                        != ModeSourceSignature(m_LoadingSources, m_LoadingSourceOrder, mode))
                {
                    MarkModeDirty(mode);
                }
            }
            m_Sources = m_LoadingSources;
            m_SourceOrder = m_LoadingSourceOrder;
            m_ConsumedVersion = m_LoadVersion;
            m_LoadingSnapshot = false;
            m_Started = true;
        }

        private static ulong ModeSourceSignature(
            Dictionary<Entity, LineSource> sources,
            List<Entity> order,
            TransitMode mode)
        {
            ulong hash = 1469598103934665603UL;
            int count = 0;
            for (int i = 0; i < (order?.Count ?? 0); i++)
            {
                if (sources == null
                    || !sources.TryGetValue(order[i], out LineSource source)
                    || source == null
                    || source.Mode != mode)
                {
                    continue;
                }
                count++;
                hash = Mix(hash, source.Line.Index);
                hash = Mix(hash, source.Line.Version);
                hash = Mix(hash, source.ChainSignature);
                hash = Mix(hash, source.TraversalSignature);
                hash = Mix(hash, source.ChainComplete ? 1 : 0);
                hash = Mix(hash, source.HasPhysicalTurnback ? 1 : 0);
                hash = Mix(hash, source.LineId);
            }
            return Mix(hash, count);
        }

        private void ApplySnapshot(
            PublishedTraversalSnapshot snapshot,
            Dictionary<Entity, LineSource> target,
            List<Entity> order)
        {
            if (snapshot == null || snapshot.Line == Entity.Null || !snapshot.Available)
            {
                if (snapshot != null)
                    RemoveSource(target, order, snapshot.Line);
                return;
            }
            TransitMode mode = TransportModeResolver.Resolve(m_Entities, snapshot.Line);
            if (!IsRailMode(mode))
            {
                RemoveSource(target, order, snapshot.Line);
                return;
            }
            LineSource source = BuildSource(snapshot, mode);
            if (source == null)
                RemoveSource(target, order, snapshot.Line);
            else
                PutSource(target, order, source);
        }

        private void ApplyChangedSnapshot(PublishedTraversalSnapshot snapshot)
        {
            TransitMode oldMode = SourceMode(m_Sources, snapshot?.Line ?? Entity.Null);
            ApplySnapshot(snapshot, m_Sources, m_SourceOrder);
            TransitMode newMode = SourceMode(m_Sources, snapshot?.Line ?? Entity.Null);
            if (IsRailMode(oldMode))
                MarkModeDirty(oldMode);
            if (IsRailMode(newMode) && newMode != oldMode)
                MarkModeDirty(newMode);
        }

        private LineSource BuildSource(PublishedTraversalSnapshot snapshot, TransitMode mode)
        {
            string lineId = m_LineId(snapshot.Line) ?? string.Empty;
            if (string.IsNullOrEmpty(lineId))
                return null;

            LineSource source = new LineSource
            {
                Line = snapshot.Line,
                LineId = lineId,
                LineIdentity = lineId + "@" + snapshot.Line.Index + ":" + snapshot.Line.Version,
                Mode = mode,
                ChainSignature = snapshot.ChainSignature,
                TraversalSignature = snapshot.TraversalSignature,
                ChainComplete = snapshot.ChainComplete,
                HasPhysicalTurnback = snapshot.HasPhysicalTurnback
            };
            for (int i = 0; i < (snapshot.Events?.Length ?? 0); i++)
            {
                TraversalEvent item = snapshot.Events[i];
                if (item.Kind != TraversalEventKind.Stop
                    && item.Kind != TraversalEventKind.Pass
                    && item.Kind != TraversalEventKind.OutsideEndpointBoundary
                    && item.Kind != TraversalEventKind.BreakBoundary)
                {
                    continue;
                }
                bool boundary = item.Kind == TraversalEventKind.BreakBoundary;
                bool outsideEndpoint = item.Kind == TraversalEventKind.OutsideEndpointBoundary;
                string stationId = boundary
                    ? string.Empty
                    : outsideEndpoint && item.Building != Entity.Null
                        ? "endpoint:" + item.Building.Index + ":" + item.Building.Version
                        : item.StationId ?? string.Empty;
                if (!boundary
                    && !outsideEndpoint
                    && string.IsNullOrEmpty(stationId)
                    && item.Building != Entity.Null)
                {
                    stationId = m_StationId(item.Building) ?? string.Empty;
                }
                string stopKey = stationId;
                if (!boundary && !outsideEndpoint && item.Building != Entity.Null)
                {
                    string resolvedStopKey = m_StationId(item.Building) ?? string.Empty;
                    if (!string.IsNullOrEmpty(resolvedStopKey))
                        stopKey = resolvedStopKey;
                }
                source.Events.Add(new Fact
                {
                    StationId = stationId,
                    StopKey = stopKey,
                    Station = item.Building,
                    Name = boundary || item.Building == Entity.Null
                        ? string.Empty
                        : m_StationName(item.Building) ?? string.Empty,
                    IsStop = item.Kind == TraversalEventKind.Stop
                        || item.Kind == TraversalEventKind.OutsideEndpointBoundary
                        || item.WaypointIndex >= 0,
                    EventOrder = item.EventIndex,
                    WaypointIndex = item.WaypointIndex,
                    StartAtomIndex = item.StartAtomIndex,
                    Broken = boundary || string.IsNullOrEmpty(stationId)
                });
            }
            source.Events.Sort((left, right) =>
            {
                int result = left.StartAtomIndex.CompareTo(right.StartAtomIndex);
                return result != 0
                    ? result
                    : left.EventOrder.CompareTo(right.EventOrder);
            });
            HashSet<int> phaseBreaks = FindPhaseBreaks(source);
            AddRegionBreaks(phaseBreaks, snapshot.RunChartTurnbackRegions, source);
            bool closeLastPhase = CanCloseLastPhase(source, snapshot.RunChartTurnbackRegions);
            bool canWrap = source.ChainComplete
                && !source.HasPhysicalTurnback
                && !source.Events.Any(item => item.Broken)
                && (snapshot.RunChartTurnbackRegions?.Length ?? 0) == 0
                && phaseBreaks.Count == 0;
            BuildPhases(source, phaseBreaks, canWrap, closeLastPhase ? source.Events[0] : null);
            return source;
        }

        private static HashSet<int> FindPhaseBreaks(LineSource source)
        {
            HashSet<int> result = new HashSet<int>();
            Dictionary<string, int> firstHop = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index + 1 < source.Events.Count; index++)
            {
                Fact from = source.Events[index];
                Fact to = source.Events[index + 1];
                if (from.Broken || to.Broken || from.StationId == to.StationId)
                    continue;
                string key = StationPairKey(from.StationId, to.StationId);
                if (!firstHop.ContainsKey(key))
                    firstHop[key] = index;
            }
            bool reversed = false;
            for (int index = 1; index + 1 < source.Events.Count; index++)
            {
                Fact from = source.Events[index];
                Fact to = source.Events[index + 1];
                if (from.Broken || to.Broken || from.StationId == to.StationId)
                {
                    reversed = false;
                    continue;
                }
                bool nowReversed = firstHop.TryGetValue(
                    StationPairKey(to.StationId, from.StationId),
                    out int firstReverse)
                    && firstReverse < index;
                if (nowReversed && !reversed)
                    result.Add(index);
                reversed = nowReversed;
            }
            return result;
        }

        private static void AddRegionBreaks(
            HashSet<int> phaseBreaks,
            RunChartTurnbackRegion[] regions,
            LineSource source)
        {
            if (phaseBreaks == null || regions == null)
                return;
            for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
            {
                RunChartTurnbackRegion region = regions[regionIndex];
                RemoveRegionBreaks(phaseBreaks, region, source);
                int boundaryAtom = region.BoundaryAtomIndex;
                for (int eventIndex = 1; eventIndex < source.Events.Count; eventIndex++)
                {
                    if (source.Events[eventIndex].StartAtomIndex < boundaryAtom)
                        continue;
                    phaseBreaks.Add(eventIndex - 1);
                    break;
                }
            }
        }

        private static void RemoveRegionBreaks(
            HashSet<int> phaseBreaks,
            RunChartTurnbackRegion region,
            LineSource source)
        {
            foreach (int breakIndex in phaseBreaks.ToArray())
            {
                int rightEventIndex = breakIndex + 1;
                if (rightEventIndex < 0 || rightEventIndex >= source.Events.Count)
                    continue;
                int rightStartAtom = source.Events[rightEventIndex].StartAtomIndex;
                if (rightStartAtom >= region.StartAtomIndex
                    && rightStartAtom < region.EndAtomIndexExclusive
                    && rightStartAtom != region.BoundaryAtomIndex)
                {
                    phaseBreaks.Remove(breakIndex);
                }
            }
        }

        private static bool CanCloseLastPhase(
            LineSource source,
            RunChartTurnbackRegion[] regions)
        {
            if (source == null
                || !source.ChainComplete
                || source.HasPhysicalTurnback
                || source.Mode != TransitMode.Tram
                || source.Events.Count < 2
                || source.Events.Any(item => item.Broken)
                || regions == null
                || regions.Length == 0)
            {
                return false;
            }
            return true;
        }

        private static void BuildPhases(
            LineSource source,
            HashSet<int> phaseBreaks,
            bool canWrap,
            Fact lastClosingFact)
        {
            source.Phases.Clear();
            LinePhase current = null;
            for (int eventIndex = 0; eventIndex < source.Events.Count; eventIndex++)
            {
                Fact fact = source.Events[eventIndex];
                if (fact.Broken)
                {
                    AddPhase(source, current);
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    current = new LinePhase();
                    current.Events.Add(fact);
                    continue;
                }
                if (phaseBreaks.Contains(eventIndex - 1))
                {
                    Fact boundary = current.Events[current.Events.Count - 1];
                    AddPhase(source, current);
                    current = new LinePhase();
                    current.Events.Add(boundary);
                    current.Events.Add(fact);
                    continue;
                }
                current.Events.Add(fact);
            }
            AddPhase(source, current);
            bool onePhase = source.Phases.Count == 1;
            for (int phaseIndex = 0; phaseIndex < source.Phases.Count; phaseIndex++)
            {
                LinePhase phase = source.Phases[phaseIndex];
                phase.Index = phaseIndex;
                phase.CanWrap = canWrap && onePhase && phase.Events.Count > 1;
                if (lastClosingFact != null
                    && phaseIndex == source.Phases.Count - 1
                    && source.Phases.Count > 1
                    && phase.Events.Count > 1
                    && !string.Equals(
                        phase.Events[phase.Events.Count - 1].StationId,
                        lastClosingFact.StationId,
                        StringComparison.Ordinal))
                {
                    phase.ClosingFact = lastClosingFact;
                }
                if (phase.CanWrap
                    && string.Equals(
                        phase.Events[0].StationId,
                        phase.Events[phase.Events.Count - 1].StationId,
                        StringComparison.Ordinal))
                {
                    phase.Events.RemoveAt(phase.Events.Count - 1);
                }
                phase.CanWrap &= phase.Events.Count > 1;
            }
        }

        private static void AddPhase(LineSource source, LinePhase phase)
        {
            if (phase != null && phase.Events.Count > 0)
                source.Phases.Add(phase);
        }

        private bool LoadBuildSourcesWork()
        {
            int work = 0;
            while (work++ < SourceItemsPerTick && m_BuildSourceIndex < m_SourceOrder.Count)
            {
                Entity line = m_SourceOrder[m_BuildSourceIndex++];
                if (m_Sources.TryGetValue(line, out LineSource source)
                    && source != null
                    && source.Mode == m_BuildMode)
                {
                    m_BuildSources.Add(source);
                }
            }
            return m_BuildSourceIndex >= m_SourceOrder.Count;
        }

        private bool BuildFactsWork()
        {
            int work = 0;
            while (work++ < FactItemsPerTick && m_BuildSourceIndex < m_BuildSources.Count)
            {
                LineSource source = m_BuildSources[m_BuildSourceIndex];
                if (m_BuildPhaseIndex >= source.Phases.Count)
                {
                    m_BuildSourceIndex++;
                    m_BuildPhaseIndex = 0;
                    m_BuildEventIndex = 0;
                    continue;
                }
                LinePhase phase = source.Phases[m_BuildPhaseIndex];
                if (m_BuildEventIndex >= phase.Events.Count)
                {
                    m_BuildPhaseIndex++;
                    m_BuildEventIndex = 0;
                    continue;
                }
                AddStation(phase.Events[m_BuildEventIndex++]);
                if (m_BuildOverflow)
                    return false;
            }
            return m_BuildSourceIndex >= m_BuildSources.Count;
        }

        private void BeginEdgeBuild()
        {
            m_BuildSourceIndex = 0;
            m_BuildPhaseIndex = 0;
            m_BuildEventIndex = 0;
            m_BuildPhase = BuildPhase.Edges;
        }

        private bool BuildEdgesWork()
        {
            int work = 0;
            while (work++ < EdgeItemsPerTick && m_BuildSourceIndex < m_BuildSources.Count)
            {
                LineSource source = m_BuildSources[m_BuildSourceIndex];
                if (m_BuildPhaseIndex >= source.Phases.Count)
                {
                    m_BuildSourceIndex++;
                    m_BuildPhaseIndex = 0;
                    m_BuildEventIndex = 0;
                    continue;
                }
                LinePhase phase = source.Phases[m_BuildPhaseIndex];
                int edgeCount = phase.Events.Count < 2
                    ? 0
                    : phase.Events.Count - 1 + (phase.CanWrap || phase.ClosingFact != null ? 1 : 0);
                if (m_BuildEventIndex >= edgeCount)
                {
                    m_BuildPhaseIndex++;
                    m_BuildEventIndex = 0;
                    continue;
                }
                int fromIndex = m_BuildEventIndex;
                int toIndex = fromIndex + 1;
                bool closing = false;
                Fact to;
                if (toIndex >= phase.Events.Count)
                {
                    closing = true;
                    to = phase.ClosingFact ?? phase.Events[0];
                }
                else
                    to = phase.Events[toIndex];
                m_BuildEventIndex++;
                AddEdge(source, phase, phase.Events[fromIndex], to, closing);
                if (m_BuildOverflow)
                    return false;
            }
            return m_BuildSourceIndex >= m_BuildSources.Count;
        }

        private void AddStation(Fact fact)
        {
            if (fact == null || fact.Broken || string.IsNullOrEmpty(fact.StationId))
                return;
            if (m_BuildStations.TryGetValue(fact.StationId, out StationItem existing))
            {
                existing.PassOnly &= !fact.IsStop;
                if (!string.IsNullOrEmpty(fact.Name)
                    && (string.IsNullOrEmpty(existing.Name)
                        || StringComparer.Ordinal.Compare(fact.Name, existing.Name) < 0))
                {
                    existing.Station = fact.Station;
                    existing.Name = fact.Name;
                }
                return;
            }
            if (m_BuildStations.Count >= MaxDirectoryStations)
            {
                SetBuildOverflow(OverflowReason.DirectoryStationLimit, fact.StationId, null);
                return;
            }
            m_BuildStations[fact.StationId] = new StationItem
            {
                StationId = fact.StationId,
                Station = fact.Station,
                Name = fact.Name ?? string.Empty,
                PassOnly = !fact.IsStop
            };
            m_BuildNetworkParents[fact.StationId] = fact.StationId;
            m_BuildStartStations.Add(fact.StationId);
        }

        private void AddEdge(
            LineSource source,
            LinePhase phase,
            Fact from,
            Fact to,
            bool closing)
        {
            if (from == null || to == null || from.Broken || to.Broken)
                return;
            if (string.Equals(from.StationId, to.StationId, StringComparison.Ordinal))
            {
                if (IsTerminalReturn(phase, from, to, closing))
                    return;
                SetBuildOverflow(
                    OverflowReason.SameStationAdjacent,
                    from.StationId,
                    source.LineId + " phase=" + phase.Index + " fromOrder="
                        + from.EventOrder + " toOrder=" + to.EventOrder);
                return;
            }
            MergeNetworks(from.StationId, to.StationId);
            string key = StationPairKey(from.StationId, to.StationId);
            if (!m_BuildEdges.TryGetValue(key, out StationEdge edge))
            {
                if (m_BuildEdges.Count >= MaxEdges)
                {
                    SetBuildOverflow(OverflowReason.EdgeLimit, to.StationId, from.StationId + ">" + to.StationId);
                    return;
                }
                edge = new StationEdge { FromStationId = from.StationId, ToStationId = to.StationId };
                m_BuildEdges[key] = edge;
                if (!m_BuildOutgoing.TryGetValue(from.StationId, out List<StationEdge> outgoing))
                {
                    outgoing = new List<StationEdge>();
                    m_BuildOutgoing[from.StationId] = outgoing;
                }
                outgoing.Add(edge);
            }
            EdgeAttachment attachment = new EdgeAttachment
            {
                LineId = source.LineId,
                LineIdentity = source.LineIdentity,
                DirectionPhase = phase.Index,
                Phase = phase,
                FromOrder = from.EventOrder,
                ToOrder = to.EventOrder,
                FromStopKey = from.StopKey,
                ToStopKey = to.StopKey,
                FromWaypointIndex = from.WaypointIndex,
                ToWaypointIndex = to.WaypointIndex,
                FromIsStop = from.IsStop,
                ToIsStop = to.IsStop,
                ChainSignature = source.ChainSignature,
                TraversalSignature = source.TraversalSignature,
                IsClosing = closing
            };
            if (!edge.Add(attachment))
                return;
            m_BuildAttachmentCount++;
            if (m_BuildAttachmentCount > MaxAttachments)
                SetBuildOverflow(OverflowReason.AttachmentLimit, to.StationId, from.StationId + ">" + to.StationId);
        }

        private static bool IsTerminalReturn(
            LinePhase phase,
            Fact from,
            Fact to,
            bool closing)
        {
            return !closing
                && phase != null
                && !phase.CanWrap
                && phase.Events.Count > 0
                && ReferenceEquals(phase.Events[phase.Events.Count - 1], to)
                && from.WaypointIndex >= 0
                && to.WaypointIndex == 0
                && to.EventOrder > from.EventOrder
                && to.StartAtomIndex > from.StartAtomIndex;
        }

        private void MergeNetworks(string leftStationId, string rightStationId)
        {
            string leftRoot = NetworkRoot(leftStationId);
            string rightRoot = NetworkRoot(rightStationId);
            if (string.IsNullOrEmpty(leftRoot)
                || string.IsNullOrEmpty(rightRoot)
                || string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
            {
                return;
            }
            if (StringComparer.Ordinal.Compare(leftRoot, rightRoot) < 0)
                m_BuildNetworkParents[rightRoot] = leftRoot;
            else
                m_BuildNetworkParents[leftRoot] = rightRoot;
        }

        private string NetworkRoot(string stationId)
        {
            if (string.IsNullOrEmpty(stationId)
                || !m_BuildNetworkParents.TryGetValue(stationId, out string parent))
            {
                return string.Empty;
            }
            string root = stationId;
            while (!string.Equals(root, parent, StringComparison.Ordinal))
            {
                root = parent;
                if (!m_BuildNetworkParents.TryGetValue(root, out parent))
                    return string.Empty;
            }
            string current = stationId;
            while (m_BuildNetworkParents.TryGetValue(current, out string currentParent)
                && !string.Equals(current, root, StringComparison.Ordinal))
            {
                m_BuildNetworkParents[current] = root;
                current = currentParent;
            }
            return root;
        }

        private void BeginNetworkBuild()
        {
            m_BuildNetworkStations = m_BuildStations.GetEnumerator();
            m_BuildPhase = BuildPhase.Networks;
        }

        private bool BuildNetworksWork()
        {
            int work = 0;
            while (work++ < NetworkItemsPerTick)
            {
                if (!m_BuildNetworkStations.MoveNext())
                {
                    m_BuildNetworkStations = null;
                    return true;
                }
                KeyValuePair<string, StationItem> entry = m_BuildNetworkStations.Current;
                string networkId = NetworkRoot(entry.Key);
                entry.Value.NetworkId = string.IsNullOrEmpty(networkId)
                    ? entry.Key
                    : networkId;
            }
            return false;
        }

        private void BeginSectionBuild()
        {
            m_BuildStartReader = m_BuildStartStations.GetEnumerator();
            m_BuildActiveSeed = null;
            m_BuildPhase = BuildPhase.Sections;
        }

        private bool BuildSectionsWork()
        {
            int work = 0;
            while (work++ < SectionItemsPerTick)
            {
                if (m_BuildActiveSeed == null)
                {
                    if (m_BuildSectionsQueue.Count > 0)
                    {
                        m_BuildActiveSeed = m_BuildSectionsQueue.Dequeue();
                        continue;
                    }
                    if (m_BuildStartReader != null && m_BuildStartReader.MoveNext())
                    {
                        AddSeed(new List<string> { m_BuildStartReader.Current }, m_BuildStartReader.Current);
                        if (m_BuildOverflow)
                            return false;
                        continue;
                    }
                    return true;
                }

                SectionSeed seed = m_BuildActiveSeed;
                if (!m_BuildOutgoing.TryGetValue(seed.LastStationId, out List<StationEdge> edges)
                    || seed.NextEdgeIndex >= edges.Count)
                {
                    m_BuildActiveSeed = null;
                    continue;
                }
                StationEdge edge = edges[seed.NextEdgeIndex++];
                if (string.Equals(edge.ToStationId, seed.RootStationId, StringComparison.Ordinal))
                {
                    if (seed.Stations.Count > 1 && edge.HasClosingAttachment)
                        AddSection(seed.Append(edge.ToStationId));
                    if (m_BuildOverflow)
                        return false;
                    continue;
                }
                if (seed.VisitedStationIds.Contains(edge.ToStationId))
                    continue;
                if (seed.Stations.Count >= MaxSectionEvents)
                {
                    SetBuildOverflow(
                        OverflowReason.SectionLengthLimit,
                        edge.ToStationId,
                        seed.LastStationId + ">" + edge.ToStationId);
                    return false;
                }
                SectionSeed next = seed.Append(edge.ToStationId);
                AddSection(next);
                if (m_BuildOverflow)
                    return false;
                AddSeed(next.Stations, next.RootStationId);
                if (m_BuildOverflow)
                    return false;
            }
            return false;
        }

        private void AddSeed(List<string> stations, string rootStationId)
        {
            if (stations == null || stations.Count == 0)
                return;
            string key = SequenceKey(stations);
            if (!m_BuildSeedKeys.Add(key))
                return;
            if (m_BuildSectionsQueue.Count >= MaxQueuedSections)
            {
                SetBuildOverflow(OverflowReason.QueueLimit, stations[stations.Count - 1], key);
                return;
            }
            m_BuildSectionsQueue.Enqueue(new SectionSeed(stations, rootStationId));
        }

        private void AddSection(SectionSeed seed)
        {
            if (seed?.Stations == null || seed.Stations.Count < 2)
                return;
            string stableKey = SequenceKey(seed.Stations);
            string id = SectionId(m_BuildMode, seed.Stations);
            if (m_BuildSections.TryGetValue(id, out Section existing))
            {
                if (!string.Equals(existing.StableKey, stableKey, StringComparison.Ordinal))
                    SetBuildOverflow(OverflowReason.SectionIdCollision, seed.LastStationId, stableKey);
                return;
            }
            if (m_BuildSections.Count >= MaxSections)
            {
                SetBuildOverflow(OverflowReason.SectionLimit, seed.LastStationId, stableKey);
                return;
            }
            Section section = new Section
            {
                Id = id,
                Mode = m_BuildMode,
                StableKey = stableKey,
                Stations = new List<string>(seed.Stations),
                StationIsStop = seed.Stations.Select(stationId =>
                    m_BuildStations.TryGetValue(stationId, out StationItem station) && !station.PassOnly)
                    .ToList()
            };
            m_BuildSections[id] = section;
            string pairKey = StationPairKey(section.Stations[0], section.Stations[section.Stations.Count - 1]);
            if (!m_BuildSectionsByStationPair.TryGetValue(pairKey, out List<Section> byPair))
            {
                byPair = new List<Section>();
                m_BuildSectionsByStationPair[pairKey] = byPair;
            }
            byPair.Add(section);
        }

        private void BeginCoverageBuild()
        {
            m_BuildCoverageReader = m_BuildSections.Values.GetEnumerator();
            m_BuildCoverageWork = null;
            m_BuildPhase = BuildPhase.Coverage;
        }

        private bool BuildCoverageWork()
        {
            int work = 0;
            while (work++ < CoverageItemsPerTick)
            {
                if (m_BuildCoverageWork == null)
                {
                    if (m_BuildCoverageReader == null || !m_BuildCoverageReader.MoveNext())
                    {
                        m_BuildCoverageReader = null;
                        return true;
                    }
                    m_BuildCoverageWork = new CoverageWork { Section = m_BuildCoverageReader.Current };
                    continue;
                }
                CoverageWork current = m_BuildCoverageWork;
                if (current.EdgeIndex >= current.Section.Stations.Count - 1)
                {
                    m_BuildCoverageWork = null;
                    continue;
                }
                if (!TryGetEdge(
                        current.Section.Stations[current.EdgeIndex],
                        current.Section.Stations[current.EdgeIndex + 1],
                        out StationEdge edge))
                {
                    SetBuildOverflow(OverflowReason.CoverageLimit, current.Section.Stations[current.EdgeIndex], "edge-missing");
                    return false;
                }
                if (current.AttachmentIndex >= edge.Attachments.Count)
                {
                    current.EdgeIndex++;
                    current.AttachmentIndex = 0;
                    continue;
                }
                EdgeAttachment attachment = edge.Attachments[current.AttachmentIndex++];
                if (HasPreviousAttachment(current.Section, current.EdgeIndex, attachment))
                    continue;
                AddCoverage(current.Section, current.EdgeIndex, attachment);
                if (m_BuildOverflow)
                    return false;
            }
            return false;
        }

        private bool HasPreviousAttachment(Section section, int edgeIndex, EdgeAttachment attachment)
        {
            if (edgeIndex == 0
                || !TryGetEdge(section.Stations[edgeIndex - 1], section.Stations[edgeIndex], out StationEdge previous))
            {
                return false;
            }
            for (int index = 0; index < previous.Attachments.Count; index++)
            {
                if (AttachmentsFollow(previous.Attachments[index], attachment))
                    return true;
            }
            return false;
        }

        private void AddCoverage(Section section, int startIndex, EdgeAttachment first)
        {
            Coverage coverage = new Coverage
            {
                LineId = first.LineId,
                LineIdentity = first.LineIdentity,
                DirectionPhase = first.DirectionPhase,
                ChainSignature = first.ChainSignature,
                TraversalSignature = first.TraversalSignature,
                FromSectionIndex = startIndex,
                ToSectionIndex = startIndex + 1
            };
            AddCoveragePoint(
                coverage,
                startIndex,
                first.FromIsStop,
                first.FromStopKey,
                first.FromWaypointIndex);
            AddCoveragePoint(
                coverage,
                startIndex + 1,
                first.ToIsStop,
                first.ToStopKey,
                first.ToWaypointIndex);
            EdgeAttachment current = first;
            for (int edgeIndex = startIndex + 1; edgeIndex < section.Stations.Count - 1; edgeIndex++)
            {
                if (!TryGetEdge(section.Stations[edgeIndex], section.Stations[edgeIndex + 1], out StationEdge edge))
                    break;
                EdgeAttachment next = edge.Attachments.FirstOrDefault(value => AttachmentsFollow(current, value));
                if (next == null)
                    break;
                current = next;
                coverage.ToSectionIndex = edgeIndex + 1;
                AddCoveragePoint(
                    coverage,
                    edgeIndex + 1,
                    current.ToIsStop,
                    current.ToStopKey,
                    current.ToWaypointIndex);
            }
            AddClipStops(coverage, first, current);
            string key = CoverageKey(coverage);
            if (!section.CoverageKeys.Add(key))
                return;
            if (++m_BuildCoverageCount > MaxCoverages)
            {
                SetBuildOverflow(OverflowReason.CoverageLimit, section.Stations[coverage.ToSectionIndex], "coverage-limit");
                return;
            }
            section.Coverages.Add(coverage);
        }

        private static void AddCoveragePoint(
            Coverage coverage,
            int sectionIndex,
            bool isStop,
            string stopKey,
            int waypointIndex)
        {
            List<CoveragePoint> target = isStop ? coverage.Stops : coverage.Passes;
            if (!target.Any(point => point.SectionIndex == sectionIndex))
            {
                target.Add(new CoveragePoint
                {
                    StationId = isStop ? stopKey : string.Empty,
                    SectionIndex = sectionIndex,
                    WaypointIndex = waypointIndex
                });
            }
        }

        private void AddClipStops(Coverage coverage, EdgeAttachment first, EdgeAttachment last)
        {
            LinePhase phase = first.Phase;
            if (phase == null || phase.Events.Count < 2)
                return;

            if (!first.FromIsStop
                && TryFindClipStop(phase, first.FromOrder, -1, out Fact leading, out int leadingHops))
            {
                coverage.LeadingStop = new CoveragePoint
                {
                    StationId = leading.StopKey,
                    SectionIndex = coverage.FromSectionIndex - leadingHops,
                    WaypointIndex = leading.WaypointIndex
                };
            }
            if (!last.ToIsStop
                && TryFindClipStop(phase, last.ToOrder, 1, out Fact trailing, out int trailingHops))
            {
                coverage.TrailingStop = new CoveragePoint
                {
                    StationId = trailing.StopKey,
                    SectionIndex = coverage.ToSectionIndex + trailingHops,
                    WaypointIndex = trailing.WaypointIndex
                };
            }
        }

        private static bool TryFindClipStop(
            LinePhase phase,
            int eventOrder,
            int direction,
            out Fact stop,
            out int hops)
        {
            stop = null;
            hops = 0;
            int start = phase.Events.FindIndex(item => item.EventOrder == eventOrder);
            if (start < 0 || direction == 0)
                return false;

            int index = start;
            for (int step = 1; step < phase.Events.Count; step++)
            {
                index += direction;
                if (index < 0 || index >= phase.Events.Count)
                {
                    if (!phase.CanWrap)
                        return false;
                    index = index < 0 ? phase.Events.Count - 1 : 0;
                }
                Fact candidate = phase.Events[index];
                if (!candidate.IsStop)
                    continue;
                stop = candidate;
                hops = step;
                return true;
            }
            return false;
        }

        private static bool AttachmentsFollow(EdgeAttachment previous, EdgeAttachment next)
        {
            return previous != null
                && next != null
                && string.Equals(previous.LineId, next.LineId, StringComparison.Ordinal)
                && string.Equals(previous.LineIdentity, next.LineIdentity, StringComparison.Ordinal)
                && previous.DirectionPhase == next.DirectionPhase
                && previous.ChainSignature == next.ChainSignature
                && previous.TraversalSignature == next.TraversalSignature
                && previous.ToOrder == next.FromOrder;
        }

        private bool TryGetEdge(string fromStationId, string toStationId, out StationEdge edge)
        {
            return m_BuildEdges.TryGetValue(StationPairKey(fromStationId, toStationId), out edge);
        }

        private void BeginPublish()
        {
            m_NextSections = new Dictionary<string, Section>(StringComparer.Ordinal);
            m_NextSectionsByStationPair = new Dictionary<string, List<Section>>(StringComparer.Ordinal);
            m_NextStations = new Dictionary<string, StationItem>(StringComparer.Ordinal);
            m_PublishSections = m_BuildSections.GetEnumerator();
            m_PublishStationPairs = m_BuildSectionsByStationPair.GetEnumerator();
            m_PublishStations = m_BuildStations.GetEnumerator();
            m_PublishStage = 0;
            m_BuildPhase = BuildPhase.Publish;
        }

        private void PublishWork()
        {
            int work = 0;
            while (work++ < PublishItemsPerTick)
            {
                if (!BuildVersionMatches())
                {
                    DiscardBuild();
                    return;
                }
                switch (m_PublishStage)
                {
                    case 0:
                        if (m_PublishSections.MoveNext())
                        {
                            KeyValuePair<string, Section> entry = m_PublishSections.Current;
                            m_NextSections[entry.Key] = entry.Value;
                            continue;
                        }
                        m_PublishSections = null;
                        m_PublishStage++;
                        continue;
                    case 1:
                        if (m_PublishStationPairs.MoveNext())
                        {
                            KeyValuePair<string, List<Section>> entry = m_PublishStationPairs.Current;
                            m_NextSectionsByStationPair[entry.Key] = new List<Section>(entry.Value);
                            continue;
                        }
                        m_PublishStationPairs = null;
                        m_PublishStage++;
                        continue;
                    case 2:
                        if (m_PublishStations.MoveNext())
                        {
                            KeyValuePair<string, StationItem> entry = m_PublishStations.Current;
                            m_NextStations[entry.Key] = CloneStation(entry.Value);
                            continue;
                        }
                        m_PublishStations = null;
                        m_PublishStage++;
                        continue;
                    default:
                        ModeState state = State(m_BuildMode);
                        state.Published = new PublishedState
                        {
                            Sections = m_NextSections,
                            ByStationPair = m_NextSectionsByStationPair,
                            Stations = m_NextStations
                        };
                        state.PublishedVersion++;
                        state.Dirty = false;
                        state.Overflow = false;
                        ClearBuildDiagnostic();
                        FinishBuild();
                        return;
                }
            }
        }

        internal DispatchWorkbenchRunChartSectionResponseDto Query(
            DispatchWorkbenchRunChartSectionRequestDto request)
        {
            bool hasSection = !string.IsNullOrEmpty(request?.sectionId);
            if (request == null
                || !TryRailMode(request.mode, out TransitMode mode)
                || (!hasSection
                    && (string.IsNullOrEmpty(request.fromStationId)
                        || string.IsNullOrEmpty(request.toStationId)))
                || (hasSection && request.expectedIndexVersion == 0))
            {
                return SectionFailure("run-chart-section-request-invalid");
            }
            ModeState state = State(mode);
            string status = ModeStatus(state, request.expectedIndexVersion);
            List<Section> sections = new List<Section>();
            if (status != "stale" && state.Published != null)
            {
                if (hasSection)
                {
                    if (state.Published.Sections.TryGetValue(request.sectionId, out Section section)
                        && section.Mode == mode)
                    {
                        sections.Add(section);
                    }
                }
                else
                {
                    state.Published.ByStationPair.TryGetValue(
                        StationPairKey(request.fromStationId, request.toStationId),
                        out sections);
                    sections = sections ?? new List<Section>();
                }
            }
            return new DispatchWorkbenchRunChartSectionResponseDto
            {
                success = true,
                error = string.Empty,
                publishedIndexVersion = state.PublishedVersion,
                status = status,
                sections = sections.OrderBy(section => section.StableKey, StringComparer.Ordinal)
                    .Select(ToDto)
                    .ToArray(),
                truncated = false,
                truncatedPairs = Array.Empty<string>()
            };
        }

        internal DispatchWorkbenchRunChartStationDirectoryResponseDto QueryStations(
            DispatchWorkbenchRunChartStationDirectoryRequestDto request)
        {
            if (request == null || !TryRailMode(request.mode, out TransitMode mode))
            {
                return new DispatchWorkbenchRunChartStationDirectoryResponseDto
                {
                    success = false,
                    error = "run-chart-station-directory-mode-required",
                    status = "invalid",
                    publishedIndexVersion = 0,
                    stations = Array.Empty<DispatchWorkbenchRunChartStationDirectoryItemDto>()
                };
            }
            ModeState state = State(mode);
            string status = ModeStatus(state, request.expectedIndexVersion);
            IEnumerable<StationItem> stations = status == "stale" || state.Published == null
                ? Array.Empty<StationItem>()
                : state.Published.Stations.Values;
            return new DispatchWorkbenchRunChartStationDirectoryResponseDto
            {
                success = true,
                error = string.Empty,
                status = status,
                publishedIndexVersion = state.PublishedVersion,
                stations = stations.Select(item => new DispatchWorkbenchRunChartStationDirectoryItemDto
                    {
                        stationId = item.StationId,
                        networkId = item.NetworkId,
                        name = CurrentStationName(item),
                        passOnly = item.PassOnly
                    })
                    .OrderBy(item => item.name, StringComparer.Ordinal)
                    .ThenBy(item => item.stationId, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private string CurrentStationName(StationItem item)
        {
            if (item != null && item.Station != Entity.Null && m_Entities.Exists(item.Station))
            {
                string name = m_StationName(item.Station) ?? string.Empty;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return item?.Name ?? string.Empty;
        }

        internal void Clear()
        {
            m_Sources.Clear();
            m_SourceOrder.Clear();
            m_LoadingSources.Clear();
            m_LoadingSourceOrder.Clear();
            m_SnapshotBatch.Clear();
            m_Started = false;
            m_LoadingSnapshot = false;
            m_LoadIndex = 0;
            m_LoadVersion = 0;
            m_ConsumedVersion = 0;
            DiscardBuild();
            for (int index = (int)TransitMode.Train; index <= (int)TransitMode.Tram; index++)
                m_Modes[index].Reset();
        }

        private void BeginNextBuild()
        {
            for (int offset = 0; offset < 3; offset++)
            {
                int index = ((m_NextModeIndex - (int)TransitMode.Train + offset) % 3)
                    + (int)TransitMode.Train;
                ModeState state = m_Modes[index];
                if (!state.Dirty)
                    continue;
                m_NextModeIndex = index == (int)TransitMode.Tram
                    ? (int)TransitMode.Train
                    : index + 1;
                BeginBuild(state);
                return;
            }
        }

        private void BeginBuild(ModeState state)
        {
            ClearBuildData();
            m_BuildMode = state.Mode;
            m_BuildGeneration = state.InputGeneration;
            m_Building = true;
            m_BuildPhase = BuildPhase.Sources;
        }

        private void DiscardBuild()
        {
            ClearBuildData();
            m_Building = false;
            m_BuildOverflow = false;
            m_BuildPhase = BuildPhase.None;
            m_BuildMode = TransitMode.Unknown;
            m_BuildGeneration = 0;
        }

        private void FinishBuild()
        {
            ClearBuildData();
            m_Building = false;
            m_BuildOverflow = false;
            m_BuildPhase = BuildPhase.None;
            m_BuildMode = TransitMode.Unknown;
            m_BuildGeneration = 0;
        }

        private void ClearBuildData()
        {
            m_BuildSources.Clear();
            m_BuildStations.Clear();
            m_BuildEdges.Clear();
            m_BuildNetworkParents.Clear();
            m_BuildOutgoing.Clear();
            m_BuildStartStations.Clear();
            m_BuildSectionsQueue.Clear();
            m_BuildSeedKeys.Clear();
            m_BuildSections.Clear();
            m_BuildSectionsByStationPair.Clear();
            m_BuildSourceIndex = 0;
            m_BuildPhaseIndex = 0;
            m_BuildEventIndex = 0;
            m_BuildAttachmentCount = 0;
            m_BuildCoverageCount = 0;
            m_BuildStartReader = null;
            m_BuildNetworkStations = null;
            m_BuildActiveSeed = null;
            m_BuildCoverageReader = null;
            m_BuildCoverageWork = null;
            m_NextSections = null;
            m_NextSectionsByStationPair = null;
            m_NextStations = null;
            m_PublishSections = null;
            m_PublishStationPairs = null;
            m_PublishStations = null;
            m_PublishStage = 0;
        }

        private void StopOverflow()
        {
            ModeState state = State(m_BuildMode);
            LogBuildOverflow();
            DiscardBuild();
            if (state != null)
            {
                state.Dirty = false;
                state.Overflow = true;
            }
        }

        private void SetBuildOverflow(OverflowReason reason, string stationId, string detail)
        {
            if (m_BuildOverflow)
                return;
            m_BuildOverflow = true;
            m_BuildDiagnostic.Reason = reason;
            m_BuildDiagnostic.Mode = m_BuildMode;
            m_BuildDiagnostic.StationId = stationId ?? string.Empty;
            m_BuildDiagnostic.Detail = Shorten(detail, 160);
            m_BuildDiagnostic.EdgeCount = m_BuildEdges.Count;
            m_BuildDiagnostic.AttachmentCount = m_BuildAttachmentCount;
            m_BuildDiagnostic.SectionCount = m_BuildSections.Count;
            m_BuildDiagnostic.QueueCount = m_BuildSectionsQueue.Count;
        }

        private void LogBuildOverflow()
        {
            if (m_BuildDiagnostic == null || m_BuildDiagnostic.Logged)
                return;
            m_BuildDiagnostic.Logged = true;
            global::RapidTransitMod.Mod.log.Info(
                "[RunChartSectionIndexOverflow] reason=" + OverflowReasonCode(m_BuildDiagnostic.Reason)
                + " mode=" + m_BuildDiagnostic.Mode.ToString().ToLowerInvariant()
                + " station=" + m_BuildDiagnostic.StationId
                + " detail=" + m_BuildDiagnostic.Detail
                + " edges=" + m_BuildDiagnostic.EdgeCount
                + " attachments=" + m_BuildDiagnostic.AttachmentCount
                + " sections=" + m_BuildDiagnostic.SectionCount
                + " queue=" + m_BuildDiagnostic.QueueCount);
        }

        private void ClearBuildDiagnostic()
        {
            m_BuildDiagnostic = new OverflowDiagnostic();
        }

        private static DispatchWorkbenchRunChartSectionResponseDto SectionFailure(string error)
        {
            return new DispatchWorkbenchRunChartSectionResponseDto
            {
                success = false,
                error = error,
                publishedIndexVersion = 0,
                status = "invalid",
                sections = Array.Empty<DispatchWorkbenchRunChartSectionDto>(),
                truncated = false,
                truncatedPairs = Array.Empty<string>()
            };
        }

        private static bool IsRailMode(TransitMode mode)
        {
            return mode == TransitMode.Train
                || mode == TransitMode.Subway
                || mode == TransitMode.Tram;
        }

        private static bool TryRailMode(string value, out TransitMode mode)
        {
            return TransitModeCodec.TryParse(value, out mode) && IsRailMode(mode);
        }

        private ModeState State(TransitMode mode)
        {
            int index = (int)mode;
            return index >= (int)TransitMode.Train && index <= (int)TransitMode.Tram
                ? m_Modes[index]
                : null;
        }

        private void MarkModeDirty(TransitMode mode)
        {
            ModeState state = State(mode);
            if (state == null)
                return;
            state.InputGeneration++;
            state.Dirty = true;
            state.Overflow = false;
        }

        private string ModeStatus(ModeState state, ulong expectedVersion)
        {
            if (state == null)
                return "invalid";
            if (expectedVersion != 0 && expectedVersion != state.PublishedVersion)
                return "stale";
            if (state.Overflow)
                return "overflow";
            if (!m_Started
                || state.Published == null
                || state.Dirty
                || (m_Building && m_BuildMode == state.Mode))
            {
                return "warming";
            }
            return "ready";
        }

        private bool BuildVersionMatches()
        {
            ModeState state = State(m_BuildMode);
            return state != null && state.InputGeneration == m_BuildGeneration;
        }

        private static void RemoveSource(
            Dictionary<Entity, LineSource> sources,
            List<Entity> order,
            Entity line)
        {
            if (sources == null || line == Entity.Null || !sources.Remove(line) || order == null)
                return;
            order.Remove(line);
        }

        private static void PutSource(
            Dictionary<Entity, LineSource> sources,
            List<Entity> order,
            LineSource source)
        {
            bool existed = sources.ContainsKey(source.Line);
            sources[source.Line] = source;
            if (existed)
                order.Remove(source.Line);
            int insert = order.Count;
            for (int index = 0; index < order.Count; index++)
            {
                if (!sources.TryGetValue(order[index], out LineSource current)
                    || CompareSources(source, current) < 0)
                {
                    insert = index;
                    break;
                }
            }
            order.Insert(insert, source.Line);
        }

        private static TransitMode SourceMode(Dictionary<Entity, LineSource> sources, Entity line)
        {
            return line != Entity.Null
                && sources.TryGetValue(line, out LineSource source)
                && source != null
                ? source.Mode
                : TransitMode.Unknown;
        }

        private static int CompareSources(LineSource left, LineSource right)
        {
            int value = StringComparer.Ordinal.Compare(left?.LineId, right?.LineId);
            if (value != 0)
                return value;
            value = (left?.Line.Index ?? int.MinValue).CompareTo(right?.Line.Index ?? int.MinValue);
            return value != 0
                ? value
                : (left?.Line.Version ?? int.MinValue).CompareTo(right?.Line.Version ?? int.MinValue);
        }

        private static string SequenceKey(IEnumerable<string> stations)
        {
            return string.Join("\u001f", stations ?? Array.Empty<string>());
        }

        private static string StationPairKey(string fromStationId, string toStationId)
        {
            string from = fromStationId ?? string.Empty;
            string to = toStationId ?? string.Empty;
            return from.Length + ":" + from + "|" + to.Length + ":" + to;
        }

        private static string SectionId(TransitMode mode, IEnumerable<string> stations)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, (int)mode);
            int index = 0;
            foreach (string stationId in stations ?? Array.Empty<string>())
            {
                hash = Mix(hash, stationId);
                hash = Mix(hash, index++);
            }
            return hash.ToString("x16");
        }

        private static string AttachmentKey(EdgeAttachment attachment)
        {
            return attachment.LineIdentity + "\u001f" + attachment.DirectionPhase + "\u001f"
                + attachment.FromOrder + "\u001f" + attachment.ToOrder + "\u001f"
                + (attachment.FromIsStop ? "1" : "0") + "\u001f"
                + (attachment.ToIsStop ? "1" : "0") + "\u001f"
                + attachment.ChainSignature + "\u001f" + attachment.TraversalSignature + "\u001f"
                + (attachment.IsClosing ? "1" : "0");
        }

        private static string CoverageKey(Coverage coverage)
        {
            StringBuilder key = new StringBuilder();
            key.Append(coverage.LineIdentity).Append('\u001f')
                .Append(coverage.DirectionPhase).Append('\u001f')
                .Append(coverage.ChainSignature).Append('\u001f')
                .Append(coverage.TraversalSignature).Append('\u001f')
                .Append(coverage.FromSectionIndex).Append('\u001f')
                .Append(coverage.ToSectionIndex);
            return key.ToString();
        }

        private static StationItem CloneStation(StationItem item)
        {
            return new StationItem
            {
                StationId = item.StationId,
                NetworkId = item.NetworkId,
                Station = item.Station,
                Name = item.Name,
                PassOnly = item.PassOnly
            };
        }

        private static DispatchWorkbenchRunChartSectionDto ToDto(Section section)
        {
            return new DispatchWorkbenchRunChartSectionDto
            {
                sectionId = section.Id,
                mode = section.Mode.ToString().ToLowerInvariant(),
                stations = section.Stations.Select((stationId, index) => new DispatchWorkbenchRunChartStationDto
                {
                    stationId = stationId,
                    sectionIndex = index,
                    waypointIndex = -1,
                    type = section.StationIsStop[index] ? "stop" : "pass"
                }).ToArray(),
                coverages = section.Coverages.Select(coverage => new DispatchWorkbenchRunChartCoverageDto
                {
                    lineId = coverage.LineId,
                    lineIdentity = coverage.LineIdentity,
                    mode = section.Mode.ToString().ToLowerInvariant(),
                    directionPhase = coverage.DirectionPhase,
                    chainSignature = coverage.ChainSignature,
                    traversalSignature = coverage.TraversalSignature,
                    fromSectionIndex = coverage.FromSectionIndex,
                    toSectionIndex = coverage.ToSectionIndex,
                    stops = coverage.Stops.Select(point => new DispatchWorkbenchRunChartStationDto
                    {
                        stationId = point.StationId,
                        sectionIndex = point.SectionIndex,
                        waypointIndex = point.WaypointIndex,
                        type = "stop"
                    }).ToArray(),
                    passes = coverage.Passes.Select(point => new DispatchWorkbenchRunChartStationDto
                    {
                        stationId = section.Stations[point.SectionIndex],
                        sectionIndex = point.SectionIndex,
                        waypointIndex = -1,
                        type = "pass"
                    }).ToArray(),
                    leadingStop = ClipStopDto(coverage.LeadingStop),
                    trailingStop = ClipStopDto(coverage.TrailingStop)
                }).ToArray()
            };
        }

        private static DispatchWorkbenchRunChartStationDto ClipStopDto(CoveragePoint point)
        {
            return point == null
                ? null
                : new DispatchWorkbenchRunChartStationDto
                {
                    stationId = point.StationId,
                    sectionIndex = point.SectionIndex,
                    waypointIndex = point.WaypointIndex,
                    type = "clip"
                };
        }

        private static string OverflowReasonCode(OverflowReason reason)
        {
            switch (reason)
            {
                case OverflowReason.DirectoryStationLimit: return "directory-station-limit";
                case OverflowReason.EdgeLimit: return "edge-limit";
                case OverflowReason.AttachmentLimit: return "attachment-limit";
                case OverflowReason.SameStationAdjacent: return "same-station-adjacent";
                case OverflowReason.QueueLimit: return "section-queue-limit";
                case OverflowReason.SectionLimit: return "section-limit";
                case OverflowReason.SectionLengthLimit: return "section-length-limit";
                case OverflowReason.SectionIdCollision: return "section-id-collision";
                case OverflowReason.CoverageLimit: return "coverage-limit";
                default: return "unknown";
            }
        }

        private static string Shorten(string value, int length)
        {
            string text = value ?? string.Empty;
            return text.Length <= length ? text : text.Substring(0, length) + "...";
        }

        private static ulong Mix(ulong hash, int value)
        {
            unchecked { return (hash ^ (uint)value) * 1099511628211UL; }
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            return Mix(Mix(hash, (int)value), (int)(value >> 32));
        }

        private static ulong Mix(ulong hash, string value)
        {
            foreach (char character in value ?? string.Empty)
                hash = Mix(hash, character);
            return hash;
        }

        private sealed class LineSource
        {
            internal Entity Line;
            internal string LineId;
            internal string LineIdentity;
            internal TransitMode Mode;
            internal ulong ChainSignature;
            internal ulong TraversalSignature;
            internal bool ChainComplete;
            internal bool HasPhysicalTurnback;
            internal readonly List<Fact> Events = new List<Fact>();
            internal readonly List<LinePhase> Phases = new List<LinePhase>();
        }

        private sealed class LinePhase
        {
            internal int Index;
            internal bool CanWrap;
            // 仅完整链尾部的私有区域回程阶段可闭合到既有首事件一次。
            internal Fact ClosingFact;
            internal readonly List<Fact> Events = new List<Fact>();
        }

        private sealed class Fact
        {
            internal string StationId;
            internal string StopKey;
            internal Entity Station;
            internal string Name;
            internal bool IsStop;
            internal int EventOrder;
            internal int WaypointIndex = -1;
            internal int StartAtomIndex;
            internal bool Broken;
        }

        private sealed class StationEdge
        {
            private readonly HashSet<string> m_AttachmentKeys = new HashSet<string>(StringComparer.Ordinal);
            internal string FromStationId;
            internal string ToStationId;
            internal bool HasClosingAttachment;
            internal readonly List<EdgeAttachment> Attachments = new List<EdgeAttachment>();

            internal bool Add(EdgeAttachment attachment)
            {
                if (!m_AttachmentKeys.Add(AttachmentKey(attachment)))
                    return false;
                Attachments.Add(attachment);
                HasClosingAttachment |= attachment.IsClosing;
                return true;
            }
        }

        private sealed class EdgeAttachment
        {
            internal string LineId;
            internal string LineIdentity;
            internal int DirectionPhase;
            internal LinePhase Phase;
            internal int FromOrder;
            internal int ToOrder;
            internal string FromStopKey;
            internal string ToStopKey;
            internal int FromWaypointIndex;
            internal int ToWaypointIndex;
            internal bool FromIsStop;
            internal bool ToIsStop;
            internal ulong ChainSignature;
            internal ulong TraversalSignature;
            internal bool IsClosing;
        }

        private sealed class SectionSeed
        {
            internal readonly List<string> Stations;
            internal readonly HashSet<string> VisitedStationIds;
            internal readonly string RootStationId;
            internal int NextEdgeIndex;
            internal string LastStationId => Stations[Stations.Count - 1];

            internal SectionSeed(List<string> stations, string rootStationId)
            {
                Stations = new List<string>(stations ?? new List<string>());
                RootStationId = rootStationId ?? string.Empty;
                VisitedStationIds = new HashSet<string>(Stations, StringComparer.Ordinal);
            }

            internal SectionSeed Append(string stationId)
            {
                List<string> next = new List<string>(Stations) { stationId };
                return new SectionSeed(next, RootStationId);
            }
        }

        private sealed class Section
        {
            internal string Id;
            internal TransitMode Mode;
            internal string StableKey;
            internal List<string> Stations;
            internal List<bool> StationIsStop;
            internal readonly List<Coverage> Coverages = new List<Coverage>();
            internal readonly HashSet<string> CoverageKeys = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class CoverageWork
        {
            internal Section Section;
            internal int EdgeIndex;
            internal int AttachmentIndex;
        }

        private sealed class Coverage
        {
            internal string LineId;
            internal string LineIdentity;
            internal int DirectionPhase;
            internal ulong ChainSignature;
            internal ulong TraversalSignature;
            internal int FromSectionIndex;
            internal int ToSectionIndex;
            internal readonly List<CoveragePoint> Stops = new List<CoveragePoint>();
            internal readonly List<CoveragePoint> Passes = new List<CoveragePoint>();
            internal CoveragePoint LeadingStop;
            internal CoveragePoint TrailingStop;
        }

        private sealed class CoveragePoint
        {
            internal string StationId;
            internal int SectionIndex;
            internal int WaypointIndex;
        }

        private sealed class StationItem
        {
            internal string StationId;
            internal string NetworkId;
            internal Entity Station;
            internal string Name;
            internal bool PassOnly;
        }

        private sealed class ModeState
        {
            internal readonly TransitMode Mode;
            internal ulong InputGeneration;
            internal ulong PublishedVersion;
            internal bool Dirty;
            internal bool Overflow;
            internal PublishedState Published;

            internal ModeState(TransitMode mode)
            {
                Mode = mode;
            }

            internal void Reset()
            {
                InputGeneration = 0;
                PublishedVersion = 0;
                Dirty = false;
                Overflow = false;
                Published = null;
            }
        }

        private sealed class PublishedState
        {
            internal Dictionary<string, Section> Sections = new Dictionary<string, Section>(StringComparer.Ordinal);
            internal Dictionary<string, List<Section>> ByStationPair =
                new Dictionary<string, List<Section>>(StringComparer.Ordinal);
            internal Dictionary<string, StationItem> Stations =
                new Dictionary<string, StationItem>(StringComparer.Ordinal);
        }

        private sealed class OverflowDiagnostic
        {
            internal OverflowReason Reason;
            internal TransitMode Mode;
            internal string StationId = string.Empty;
            internal string Detail = string.Empty;
            internal int EdgeCount;
            internal int AttachmentCount;
            internal int SectionCount;
            internal int QueueCount;
            internal bool Logged;
        }
    }
}
