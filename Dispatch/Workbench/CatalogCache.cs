using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class CatalogCache
    {
        private const int LinesPerTick = 2;
        private const int DepotsPerTick = 4;

        private readonly Catalog m_Catalog;
        private readonly Action<DispatchWorkbenchCatalogEvent> m_Push;
        private readonly Action<DispatchWorkbenchLineInvalidationEvent> m_PushInvalidation;
        private readonly Action<DispatchWorkbenchSnapshot> m_PushSnapshot;
        private readonly Func<IReadOnlyDictionary<string, string>, bool> m_CleanupConfirmedInvalidatedLines;
        private readonly Func<IEnumerable<WorkbenchLineRuntime>, IReadOnlyDictionary<string, string>> m_CollectBackendMissingLineReasons;
        private readonly Func<ulong> m_Version;
        private readonly Func<bool> m_CanPushSnapshot;
        private readonly Func<DispatchWorkbenchSnapshot> m_BuildSnapshot;
        private readonly Queue<Entity> m_StationQueue = new Queue<Entity>();
        private readonly HashSet<Entity> m_QueuedStations = new HashSet<Entity>();
        private readonly Dictionary<Entity, List<DispatchWorkbenchStationDto>> m_Stations =
            new Dictionary<Entity, List<DispatchWorkbenchStationDto>>();
        private List<WorkbenchLineRuntime> m_Lines = new List<WorkbenchLineRuntime>();
        private List<DispatchWorkbenchDepotDto> m_Depots = new List<DispatchWorkbenchDepotDto>();
        private Entity[] m_LineRebuildEntities = Array.Empty<Entity>();
        private Entity[] m_DepotRebuildEntities = Array.Empty<Entity>();
        private int m_LineRebuildIndex;
        private int m_DepotRebuildIndex;
        private List<WorkbenchLineRuntime> m_LineRebuildResult;
        private List<DispatchWorkbenchDepotDto> m_DepotRebuildResult;
        private HashSet<Entity> m_DepotRebuildSeen;
        private ulong m_LineGeneration;
        private ulong m_DepotGeneration;
        private ulong m_LineRebuildGeneration;
        private ulong m_DepotRebuildGeneration;
        private bool m_LinesReady;
        private bool m_DepotsReady;
        private bool m_LinesStale = true;
        private bool m_DepotsStale = true;
        private bool m_StationsStale = true;
        private bool m_PendingEvent;
        private bool m_HasConfirmedLineBaseline;
        private readonly Dictionary<string, string> m_ConfirmedLineSignatures =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_LineInvalidationCandidates =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_BackendMissingLineCandidates =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_PendingInvalidationReasons =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal CatalogCache(
            Catalog catalog,
            Action<DispatchWorkbenchCatalogEvent> push,
            Action<DispatchWorkbenchLineInvalidationEvent> pushInvalidation,
            Action<DispatchWorkbenchSnapshot> pushSnapshot,
            Func<IReadOnlyDictionary<string, string>, bool> cleanupConfirmedInvalidatedLines,
            Func<IEnumerable<WorkbenchLineRuntime>, IReadOnlyDictionary<string, string>> collectBackendMissingLineReasons,
            Func<ulong> version,
            Func<bool> canPushSnapshot,
            Func<DispatchWorkbenchSnapshot> buildSnapshot)
        {
            m_Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            m_Push = push ?? throw new ArgumentNullException(nameof(push));
            m_PushInvalidation = pushInvalidation ?? throw new ArgumentNullException(nameof(pushInvalidation));
            m_PushSnapshot = pushSnapshot ?? throw new ArgumentNullException(nameof(pushSnapshot));
            m_CleanupConfirmedInvalidatedLines = cleanupConfirmedInvalidatedLines ?? throw new ArgumentNullException(nameof(cleanupConfirmedInvalidatedLines));
            m_CollectBackendMissingLineReasons = collectBackendMissingLineReasons ?? throw new ArgumentNullException(nameof(collectBackendMissingLineReasons));
            m_Version = version ?? throw new ArgumentNullException(nameof(version));
            m_CanPushSnapshot = canPushSnapshot ?? throw new ArgumentNullException(nameof(canPushSnapshot));
            m_BuildSnapshot = buildSnapshot ?? throw new ArgumentNullException(nameof(buildSnapshot));
        }

        internal void Reset()
        {
            m_Lines.Clear();
            m_Depots.Clear();
            m_Stations.Clear();
            m_StationQueue.Clear();
            m_QueuedStations.Clear();
            m_LineRebuildEntities = Array.Empty<Entity>();
            m_DepotRebuildEntities = Array.Empty<Entity>();
            m_LineRebuildIndex = 0;
            m_DepotRebuildIndex = 0;
            m_LineRebuildResult = null;
            m_DepotRebuildResult = null;
            m_DepotRebuildSeen = null;
            m_LineGeneration++;
            m_DepotGeneration++;
            m_LineRebuildGeneration = m_LineGeneration;
            m_DepotRebuildGeneration = m_DepotGeneration;
            m_LinesReady = false;
            m_DepotsReady = false;
            m_LinesStale = true;
            m_DepotsStale = true;
            m_StationsStale = true;
            m_PendingEvent = false;
            m_HasConfirmedLineBaseline = false;
            m_ConfirmedLineSignatures.Clear();
            m_LineInvalidationCandidates.Clear();
            m_BackendMissingLineCandidates.Clear();
            m_PendingInvalidationReasons.Clear();
        }

        internal void MarkDirty()
        {
            m_LinesStale = true;
            m_DepotsStale = true;
            m_StationsStale = true;
            m_LineGeneration++;
            m_DepotGeneration++;
            m_Stations.Clear();
            m_StationQueue.Clear();
            m_QueuedStations.Clear();
            m_PendingEvent = true;
        }

        internal void RefreshNow()
        {
            CancelLineRebuild();
            CancelDepotRebuild();
            m_LineGeneration++;
            m_DepotGeneration++;
            m_Lines = m_Catalog.RuntimeLines();
            m_Depots = m_Catalog.Depots()
                .OrderBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.id, StringComparer.Ordinal)
                .ToList();
            m_Stations.Clear();
            m_StationQueue.Clear();
            m_QueuedStations.Clear();
            m_LinesReady = true;
            m_DepotsReady = true;
            m_LinesStale = false;
            m_DepotsStale = false;
            m_StationsStale = true;
            InitializeConfirmedLineBaseline(m_Lines);
            QueueBackendMissingLineVerification(m_Lines);
        }

        internal void Tick(uint nowFrame)
        {
            if (m_LinesStale && m_LineRebuildResult == null)
            {
                StartLineRebuild();
            }

            if (m_DepotsStale && m_DepotRebuildResult == null)
            {
                StartDepotRebuild();
            }

            bool changed = false;
            changed |= TickLines();
            changed |= TickDepots();
            changed |= TickStations();

            if (m_PendingEvent && IsReadyToPush(changed))
            {
                m_PendingEvent = false;
                if (m_PendingInvalidationReasons.Count > 0)
                {
                    PushInvalidations();
                    Push(TransitMode.Train);
                    Push(TransitMode.Subway);
                }
                else if (m_CanPushSnapshot())
                {
                    m_PushSnapshot(m_BuildSnapshot());
                }
                else
                {
                    Push(TransitMode.Train);
                    Push(TransitMode.Subway);
                }
            }
        }

        internal List<WorkbenchLineRuntime> RuntimeLines()
        {
            if (!m_LinesReady)
            {
                CancelLineRebuild();
                m_LineGeneration++;
                m_Lines = m_Catalog.RuntimeLines();
                m_LinesReady = true;
                m_LinesStale = false;
                InitializeConfirmedLineBaseline(m_Lines);
                QueueBackendMissingLineVerification(m_Lines);
                return CopyLines(m_Lines);
            }

            if (m_LinesStale)
            {
                StartLineRebuild();
            }

            return CopyLines(m_Lines);
        }

        internal List<DispatchWorkbenchDepotDto> Depots()
        {
            if (!m_DepotsReady)
            {
                CancelDepotRebuild();
                m_DepotGeneration++;
                m_Depots = m_Catalog.Depots();
                m_DepotsReady = true;
                m_DepotsStale = false;
                return CopyDepots(m_Depots);
            }

            if (m_DepotsStale)
            {
                StartDepotRebuild();
            }

            return CopyDepots(m_Depots);
        }

        internal List<DispatchWorkbenchStationDto> Stations(Entity line)
        {
            if (line == Entity.Null)
            {
                return new List<DispatchWorkbenchStationDto>();
            }

            if (!m_StationsStale && m_Stations.TryGetValue(line, out List<DispatchWorkbenchStationDto> cached))
            {
                return CopyStations(cached);
            }

            if (m_Stations.TryGetValue(line, out cached))
            {
                EnqueueStation(line);
                return CopyStations(cached);
            }

            List<DispatchWorkbenchStationDto> stations = m_Catalog.Stations(line);
            m_Stations[line] = stations;
            if (m_StationQueue.Count == 0)
            {
                m_StationsStale = false;
            }
            return CopyStations(stations);
        }

        private bool TickLines()
        {
            if (m_LineRebuildResult == null)
            {
                return false;
            }

            int end = Math.Min(m_LineRebuildIndex + LinesPerTick, m_LineRebuildEntities.Length);
            for (; m_LineRebuildIndex < end; m_LineRebuildIndex++)
            {
                if (m_Catalog.TryRuntimeLine(m_LineRebuildEntities[m_LineRebuildIndex], out WorkbenchLineRuntime line))
                {
                    m_LineRebuildResult.Add(line);
                }
            }

            if (m_LineRebuildIndex < m_LineRebuildEntities.Length)
            {
                return false;
            }

            if (m_LineRebuildGeneration != m_LineGeneration)
            {
                CancelLineRebuild();
                return false;
            }

            Dictionary<string, string> confirmedInvalidations = ConfirmStableLineInvalidations(m_LineRebuildResult);
            Dictionary<string, string> confirmedBackendMissing = ConfirmStableBackendMissingLines(m_LineRebuildResult);
            foreach (KeyValuePair<string, string> entry in confirmedBackendMissing)
            {
                if (!confirmedInvalidations.ContainsKey(entry.Key))
                {
                    confirmedInvalidations[entry.Key] = entry.Value;
                }
            }

            bool needsBackendVerification = m_BackendMissingLineCandidates.Count > 0;
            if (confirmedInvalidations.Count > 0)
            {
                if (RtLog.VerboseEnabled)
                {
                    string detail = string.Join(
                        ", ",
                        confirmedInvalidations
                            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .Select(entry => entry.Key + "=" + entry.Value));
                    Mod.log.Info("[WorkbenchLineInvalidation] confirmed " + detail);
                }

                foreach (KeyValuePair<string, string> entry in confirmedInvalidations)
                {
                    m_PendingInvalidationReasons[entry.Key] = entry.Value;
                }

                m_CleanupConfirmedInvalidatedLines(confirmedInvalidations);
                m_PendingEvent = true;
            }

            m_Lines = m_LineRebuildResult;
            m_LinesReady = true;
            m_LinesStale = false;
            CancelLineRebuild();
            if (needsBackendVerification)
            {
                m_LinesStale = true;
                m_PendingEvent = true;
            }
            return true;
        }

        private bool TickDepots()
        {
            if (m_DepotRebuildResult == null)
            {
                return false;
            }

            int end = Math.Min(m_DepotRebuildIndex + DepotsPerTick, m_DepotRebuildEntities.Length);
            for (; m_DepotRebuildIndex < end; m_DepotRebuildIndex++)
            {
                if (m_Catalog.TryDepot(m_DepotRebuildEntities[m_DepotRebuildIndex], m_DepotRebuildSeen, out DispatchWorkbenchDepotDto depot))
                {
                    m_DepotRebuildResult.Add(depot);
                }
            }

            if (m_DepotRebuildIndex < m_DepotRebuildEntities.Length)
            {
                return false;
            }

            if (m_DepotRebuildGeneration != m_DepotGeneration)
            {
                CancelDepotRebuild();
                return false;
            }

            m_Depots = m_DepotRebuildResult
                .OrderBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.id, StringComparer.Ordinal)
                .ToList();
            m_DepotsReady = true;
            m_DepotsStale = false;
            CancelDepotRebuild();
            return true;
        }

        private bool TickStations()
        {
            if (m_StationQueue.Count == 0)
            {
                return false;
            }

            Entity line = m_StationQueue.Dequeue();
            m_QueuedStations.Remove(line);
            m_Stations[line] = m_Catalog.Stations(line);
            if (m_StationQueue.Count == 0)
            {
                m_StationsStale = false;
            }
            return true;
        }

        private void StartLineRebuild()
        {
            if (m_LineRebuildResult != null)
            {
                return;
            }

            NativeArray<Entity> entities = m_Catalog.LineEntities(Allocator.Temp);
            try
            {
                m_LineRebuildEntities = entities.ToArray();
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            m_LineRebuildIndex = 0;
            m_LineRebuildGeneration = m_LineGeneration;
            m_LineRebuildResult = new List<WorkbenchLineRuntime>();
        }

        private void StartDepotRebuild()
        {
            if (m_DepotRebuildResult != null)
            {
                return;
            }

            NativeArray<Entity> entities = m_Catalog.DepotEntities(Allocator.Temp);
            try
            {
                m_DepotRebuildEntities = entities.ToArray();
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            m_DepotRebuildIndex = 0;
            m_DepotRebuildGeneration = m_DepotGeneration;
            m_DepotRebuildResult = new List<DispatchWorkbenchDepotDto>();
            m_DepotRebuildSeen = new HashSet<Entity>();
        }

        private void CancelLineRebuild()
        {
            m_LineRebuildResult = null;
            m_LineRebuildEntities = Array.Empty<Entity>();
            m_LineRebuildIndex = 0;
        }

        private void CancelDepotRebuild()
        {
            m_DepotRebuildResult = null;
            m_DepotRebuildSeen = null;
            m_DepotRebuildEntities = Array.Empty<Entity>();
            m_DepotRebuildIndex = 0;
        }

        private void EnqueueStation(Entity line)
        {
            if (line == Entity.Null || !m_QueuedStations.Add(line))
            {
                return;
            }

            m_StationQueue.Enqueue(line);
        }

        private bool IsIdle()
        {
            return m_LineRebuildResult == null
                && m_DepotRebuildResult == null
                && m_StationQueue.Count == 0;
        }

        private bool IsReadyToPush(bool changed)
        {
            return changed
                && !m_LinesStale
                && !m_DepotsStale
                && IsIdle();
        }

        private void Push(TransitMode mode)
        {
            m_Push(new DispatchWorkbenchCatalogEvent
            {
                mode = TransitModeCodec.Format(mode),
                version = m_Version().ToString()
            });
        }

        private void PushInvalidations()
        {
            if (m_PendingInvalidationReasons.Count == 0)
            {
                return;
            }

            DispatchWorkbenchLineInvalidationEvent payload = new DispatchWorkbenchLineInvalidationEvent
            {
                version = m_Version().ToString(),
                lineIds = m_PendingInvalidationReasons.Keys
                    .OrderBy(lineId => lineId, StringComparer.Ordinal)
                    .ToArray(),
                reasons = m_PendingInvalidationReasons
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new DispatchWorkbenchCleanupReasonDto
                    {
                        lineId = entry.Key,
                        reason = entry.Value
                    })
                    .ToArray()
            };

            payload.mode = TransitModeCodec.Format(TransitMode.Train);
            m_PushInvalidation(payload);
            payload.mode = TransitModeCodec.Format(TransitMode.Subway);
            m_PushInvalidation(payload);
            m_PendingInvalidationReasons.Clear();
        }

        private void InitializeConfirmedLineBaseline(IEnumerable<WorkbenchLineRuntime> lines)
        {
            m_ConfirmedLineSignatures.Clear();
            foreach (KeyValuePair<string, string> entry in BuildStableLineSignatures(lines))
            {
                m_ConfirmedLineSignatures[entry.Key] = entry.Value;
            }

            m_LineInvalidationCandidates.Clear();
            m_HasConfirmedLineBaseline = true;
        }

        private void QueueBackendMissingLineVerification(IEnumerable<WorkbenchLineRuntime> lines)
        {
            ConfirmStableBackendMissingLines(lines);
            if (m_BackendMissingLineCandidates.Count > 0)
            {
                m_LinesStale = true;
                m_PendingEvent = true;
            }
        }

        private Dictionary<string, string> ConfirmStableLineInvalidations(IEnumerable<WorkbenchLineRuntime> lines)
        {
            Dictionary<string, string> currentSignatures = BuildStableLineSignatures(lines);
            if (!m_HasConfirmedLineBaseline)
            {
                InitializeConfirmedLineBaseline(lines);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dictionary<string, string> nextConfirmedBaseline =
                new Dictionary<string, string>(m_ConfirmedLineSignatures, StringComparer.Ordinal);
            HashSet<string> lineIds = new HashSet<string>(
                m_ConfirmedLineSignatures.Keys.Concat(currentSignatures.Keys).Concat(m_LineInvalidationCandidates.Keys),
                StringComparer.Ordinal);
            Dictionary<string, string> confirmed =
                new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string lineId in lineIds.Where(lineId => !string.IsNullOrEmpty(lineId)))
            {
                bool hasBaseline = m_ConfirmedLineSignatures.TryGetValue(lineId, out string baselineSignature);
                bool hasCurrent = currentSignatures.TryGetValue(lineId, out string currentSignature);
                string candidateSignature = hasCurrent ? currentSignature : "__missing__";

                if (!hasBaseline)
                {
                    m_LineInvalidationCandidates.Remove(lineId);
                    if (hasCurrent)
                    {
                        nextConfirmedBaseline[lineId] = currentSignature;
                    }
                    else
                    {
                        nextConfirmedBaseline.Remove(lineId);
                    }

                    continue;
                }

                if (hasCurrent && string.Equals(baselineSignature, currentSignature, StringComparison.Ordinal))
                {
                    m_LineInvalidationCandidates.Remove(lineId);
                    nextConfirmedBaseline[lineId] = currentSignature;
                    continue;
                }

                if (hasCurrent)
                {
                    // Existing structure owners handle structural edits. Workbench only preserves
                    // business state while the same stable Lak remains in the live catalog.
                    m_LineInvalidationCandidates.Remove(lineId);
                    nextConfirmedBaseline[lineId] = currentSignature;
                    continue;
                }

                if (m_LineInvalidationCandidates.TryGetValue(lineId, out string pendingSignature)
                    && string.Equals(pendingSignature, candidateSignature, StringComparison.Ordinal))
                {
                    confirmed[lineId] = "runtime-line-missing";
                    m_LineInvalidationCandidates.Remove(lineId);
                    nextConfirmedBaseline.Remove(lineId);

                    continue;
                }

                m_LineInvalidationCandidates[lineId] = candidateSignature;
            }

            m_ConfirmedLineSignatures.Clear();
            foreach (KeyValuePair<string, string> entry in nextConfirmedBaseline)
            {
                m_ConfirmedLineSignatures[entry.Key] = entry.Value;
            }

            return confirmed;
        }

        private Dictionary<string, string> ConfirmStableBackendMissingLines(IEnumerable<WorkbenchLineRuntime> lines)
        {
            IReadOnlyDictionary<string, string> backendMissingReasons =
                m_CollectBackendMissingLineReasons(lines)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> missingLineIds = new HashSet<string>(
                backendMissingReasons.Keys
                    .Select(DraftStore.GetKey)
                    .Where(lineId => !string.IsNullOrEmpty(lineId) && !string.Equals(lineId, "__default__", StringComparison.Ordinal)),
                StringComparer.Ordinal);

            foreach (string lineId in m_BackendMissingLineCandidates
                .Where(lineId => !missingLineIds.Contains(lineId))
                .ToArray())
            {
                m_BackendMissingLineCandidates.Remove(lineId);
            }

            Dictionary<string, string> confirmed =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string lineId in missingLineIds.OrderBy(lineId => lineId, StringComparer.Ordinal))
            {
                if (!m_BackendMissingLineCandidates.Add(lineId))
                {
                    m_BackendMissingLineCandidates.Remove(lineId);
                    confirmed[lineId] = backendMissingReasons.TryGetValue(lineId, out string reason)
                        && !string.IsNullOrEmpty(reason)
                        ? reason
                        : "runtime-line-missing";
                }
            }

            return confirmed;
        }

        private static Dictionary<string, string> BuildStableLineSignatures(IEnumerable<WorkbenchLineRuntime> lines)
        {
            Dictionary<string, string> signatures = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (WorkbenchLineRuntime line in lines ?? Enumerable.Empty<WorkbenchLineRuntime>())
            {
                string lineId = DraftStore.GetKey(line?.Id);
                if (string.IsNullOrEmpty(lineId))
                {
                    continue;
                }

                signatures[lineId] = line?.StableSignature ?? string.Empty;
            }

            return signatures;
        }

        private static List<WorkbenchLineRuntime> CopyLines(IEnumerable<WorkbenchLineRuntime> lines)
        {
            return (lines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                .Select(Query.CopyLine)
                .Where(line => line != null)
                .ToList();
        }

        private static List<DispatchWorkbenchDepotDto> CopyDepots(IEnumerable<DispatchWorkbenchDepotDto> depots)
        {
            return (depots ?? Enumerable.Empty<DispatchWorkbenchDepotDto>())
                .Select(Query.CopyDepot)
                .Where(depot => depot != null)
                .ToList();
        }

        private static List<DispatchWorkbenchStationDto> CopyStations(IEnumerable<DispatchWorkbenchStationDto> stations)
        {
            return (stations ?? Enumerable.Empty<DispatchWorkbenchStationDto>())
                .Where(station => station != null)
                .Select(station => new DispatchWorkbenchStationDto
                {
                    id = station.id ?? string.Empty,
                    name = station.name ?? string.Empty,
                    order = station.order,
                    distance = station.distance,
                    hasSiding = station.hasSiding,
                    conflictAssets = station.conflictAssets
                })
                .ToList();
        }

    }
}
