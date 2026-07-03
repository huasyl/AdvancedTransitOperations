using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Game.Routes;
using Game.Simulation;
using RapidTransitMod.Dispatch.Observation;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    [DataContract]
    internal struct StationAnchorObservationDiagnosticsDto
    {
        [DataMember] public StationAnchorObservationSummaryDto summary;
        [DataMember] public StationAnchorStopDwellSummaryDto stopDwell;
        [DataMember] public StationAnchorPersistenceSummaryDto persistence;
        [DataMember] public StationAnchorGroupDto[] anchorGroups;
        [DataMember] public StationAnchorLegacyRowDto[] legacyRows;
    }

    [DataContract]
    internal struct StationAnchorObservationSummaryDto
    {
        [DataMember] public uint generatedAtFrame;
        [DataMember] public int lineCount;
        [DataMember] public int stopWaypointCount;
        [DataMember] public int anchorResolvedCount;
        [DataMember] public int anchorMissingCount;
        [DataMember] public int uniqueAnchorCount;
        [DataMember] public int duplicateAnchorOccurrenceCount;
    }

    [DataContract]
    internal struct StationAnchorStopDwellSummaryDto
    {
        [DataMember] public int legacyObservationCount;
        [DataMember] public int anchorObservationCount;
        [DataMember] public int legacySampleCount;
        [DataMember] public int anchorSampleCount;
        [DataMember] public ulong anchorMissingWriteCount;
        [DataMember] public ulong anchorRejectedOriginOrTerminalCount;
        [DataMember] public ulong suspiciousOriginOrTerminalCount;
        [DataMember] public ulong suspiciousLongDwellCount;
    }

    [DataContract]
    internal struct StationAnchorPersistenceSummaryDto
    {
        [DataMember] public int legacyBufferCount;
        [DataMember] public int legacyRestoredCount;
        [DataMember] public int anchorBufferCount;
        [DataMember] public int anchorRestoredCount;
        [DataMember] public bool legacyPreserved;
    }

    [DataContract]
    internal struct StationAnchorGroupDto
    {
        [DataMember] public string anchorObservationKey;
        [DataMember] public string stationAnchorId;
        [DataMember] public string name;
        [DataMember] public int buildingEntityIndex;
        [DataMember] public int[] stopEntityIndices;
        [DataMember] public string[] lineIds;
        [DataMember] public int[] waypointIndices;
        [DataMember] public string[] legacyKeys;
        [DataMember] public float anchorAverageMinutes;
        [DataMember] public int anchorSampleCount;
    }

    [DataContract]
    internal struct StationAnchorLegacyRowDto
    {
        [DataMember] public string lineId;
        [DataMember] public int lineEntityIndex;
        [DataMember] public int waypointIndex;
        [DataMember] public string anchorObservationKey;
        [DataMember] public string stationAnchorId;
        [DataMember] public int stopEntityIndex;
        [DataMember] public int buildingEntityIndex;
        [DataMember] public float legacyAverageMinutes;
        [DataMember] public int legacySampleCount;
        [DataMember] public float anchorAverageMinutes;
        [DataMember] public int anchorSampleCount;
        [DataMember] public string mappingStatus;
    }

    internal sealed class StationAnchorDiag
    {
        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_LineQuery;
        private readonly Query m_ObsQuery;
        private readonly Game.Simulation.SimulationSystem m_SimulationSystem;
        private readonly CitySystem m_CitySystem;
        private readonly Action<string> m_LogInfo;
        private readonly Func<Entity, string> m_GetLineId;
        private readonly Func<Entity, Entity> m_ResolveStop;
        private readonly Func<Entity, string> m_ResolveStationName;
        private readonly Func<Entity, int, ulong> m_MakeLegacyObservationKey;
        private readonly Func<Entity, int, (bool Found, string StationAnchorId, int BuildingEntityIndex)> m_ResolveAnchor;
        private readonly Func<Entity, string, string> m_MakeObservationKey;
        private readonly Func<ulong> m_GetTotalAnchorMissing;
        private readonly Func<ulong> m_GetTotalAnchorRejectedOriginOrTerminal;
        private readonly Func<ulong> m_GetTotalSuspiciousOriginOrTerminal;
        private readonly Func<ulong> m_GetTotalSuspiciousLongDwell;
        private readonly Func<int> m_GetLegacyRestoredCount;
        private readonly Func<int> m_GetAnchorRestoredCount;
        private readonly int m_FramesPerMinute;

        public StationAnchorDiag(
            EntityManager entityManager,
            EntityQuery lineQuery,
            Query obsQuery,
            SimulationSystem simulationSystem,
            CitySystem citySystem,
            Action<string> logInfo,
            Func<Entity, string> getLineId,
            Func<Entity, Entity> resolveStop,
            Func<Entity, string> resolveStationName,
            Func<Entity, int, ulong> makeLegacyObservationKey,
            Func<Entity, int, (bool Found, string StationAnchorId, int BuildingEntityIndex)> resolveAnchor,
            Func<Entity, string, string> makeObservationKey,
            Func<ulong> getTotalAnchorMissing,
            Func<ulong> getTotalAnchorRejectedOriginOrTerminal,
            Func<ulong> getTotalSuspiciousOriginOrTerminal,
            Func<ulong> getTotalSuspiciousLongDwell,
            Func<int> getLegacyRestoredCount,
            Func<int> getAnchorRestoredCount,
            int framesPerMinute)
        {
            m_EntityManager = entityManager;
            m_LineQuery = lineQuery;
            m_ObsQuery = obsQuery;
            m_SimulationSystem = simulationSystem;
            m_CitySystem = citySystem;
            m_LogInfo = logInfo;
            m_GetLineId = getLineId;
            m_ResolveStop = resolveStop;
            m_ResolveStationName = resolveStationName;
            m_MakeLegacyObservationKey = makeLegacyObservationKey;
            m_ResolveAnchor = resolveAnchor;
            m_MakeObservationKey = makeObservationKey;
            m_GetTotalAnchorMissing = getTotalAnchorMissing;
            m_GetTotalAnchorRejectedOriginOrTerminal = getTotalAnchorRejectedOriginOrTerminal;
            m_GetTotalSuspiciousOriginOrTerminal = getTotalSuspiciousOriginOrTerminal;
            m_GetTotalSuspiciousLongDwell = getTotalSuspiciousLongDwell;
            m_GetLegacyRestoredCount = getLegacyRestoredCount;
            m_GetAnchorRestoredCount = getAnchorRestoredCount;
            m_FramesPerMinute = framesPerMinute;
        }

        public void Dump()
        {
            if (!RtLog.DebugToolsEnabled)
                return;

            try
            {
                StationAnchorObservationDiagnosticsDto diagnostics = Build();
                string json = Workbenches.Json.Write(diagnostics);
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-station-anchor-observation-latest.json");
                File.WriteAllText(filePath, json);
                m_LogInfo("[StationAnchorDiagDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                m_LogInfo("[StationAnchorDiagDump] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public StationAnchorObservationDiagnosticsDto Build()
        {
            List<StationAnchorLegacyRowDto> legacyRows = new List<StationAnchorLegacyRowDto>();
            Dictionary<string, int> groupIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> groupObservationKeys = new List<string>();
            List<string> groupStationAnchorIds = new List<string>();
            List<string> groupNames = new List<string>();
            List<int> groupBuildingEntityIndices = new List<int>();
            List<HashSet<int>> groupStopEntityIndices = new List<HashSet<int>>();
            List<HashSet<string>> groupLineIds = new List<HashSet<string>>();
            List<List<int>> groupWaypointIndices = new List<List<int>>();
            List<List<string>> groupLegacyKeys = new List<List<string>>();
            int lineCount = 0;
            int stopWaypointCount = 0;
            int anchorResolvedCount = 0;
            int anchorMissingCount = 0;

            using Unity.Collections.NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                Entity line = lines[lineIndex];
                if (line == Entity.Null
                    || !m_EntityManager.Exists(line)
                    || !m_EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                lineCount++;
                string lineId = m_GetLineId(line);
                DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    Entity stopEntity = m_ResolveStop(waypoints[waypointIndex].m_Waypoint);
                    if (stopEntity == Entity.Null)
                        continue;

                    stopWaypointCount++;
                    bool hasLegacy = m_ObsQuery.TryWaypointDwell(m_MakeLegacyObservationKey(line, waypointIndex), out DwellObservation legacyObservation)
                        && legacyObservation.SampleCount > 0
                        && legacyObservation.AverageFrames > 0f;

                    string mappingStatus = "missing-anchor";
                    string observationKey = string.Empty;
                    string stationAnchorId = string.Empty;
                    int buildingEntityIndex = -1;
                    int stopEntityIndex = stopEntity.Index;
                    float anchorAverageMinutes = 0f;
                    int anchorSampleCount = 0;
                    (bool anchorFound, string resolvedAnchorId, int resolvedBuildingEntityIndex) = m_ResolveAnchor(line, waypointIndex);
                    if (anchorFound)
                    {
                        anchorResolvedCount++;
                        stationAnchorId = resolvedAnchorId;
                        buildingEntityIndex = resolvedBuildingEntityIndex;
                        mappingStatus = hasLegacy ? "mapped" : "mapped-no-legacy";
                        observationKey = m_MakeObservationKey(line, stationAnchorId);

                        if (m_ObsQuery.TryStationDwell(observationKey, out StationDwellObservation anchorObservation)
                            && anchorObservation.SampleCount > 0
                            && anchorObservation.AverageFrames > 0f)
                        {
                            anchorAverageMinutes = RoundMinutes(anchorObservation.AverageFrames);
                            anchorSampleCount = anchorObservation.SampleCount;
                        }

                        if (!groupIndexes.TryGetValue(observationKey, out int groupIndex))
                        {
                            groupIndex = groupObservationKeys.Count;
                            groupIndexes[observationKey] = groupIndex;
                            groupObservationKeys.Add(observationKey);
                            groupStationAnchorIds.Add(stationAnchorId);
                            groupNames.Add(m_ResolveStationName(stopEntity));
                            groupBuildingEntityIndices.Add(buildingEntityIndex);
                            groupStopEntityIndices.Add(new HashSet<int>());
                            groupLineIds.Add(new HashSet<string>(StringComparer.Ordinal));
                            groupWaypointIndices.Add(new List<int>());
                            groupLegacyKeys.Add(new List<string>());
                        }

                        groupStopEntityIndices[groupIndex].Add(stopEntityIndex);
                        if (!string.IsNullOrEmpty(lineId))
                            groupLineIds[groupIndex].Add(lineId);
                        groupWaypointIndices[groupIndex].Add(waypointIndex);
                        groupLegacyKeys[groupIndex].Add(line.Index.ToString() + ":" + waypointIndex);
                        if (groupBuildingEntityIndices[groupIndex] < 0 && buildingEntityIndex >= 0)
                            groupBuildingEntityIndices[groupIndex] = buildingEntityIndex;
                    }
                    else
                    {
                        anchorMissingCount++;
                    }

                    legacyRows.Add(new StationAnchorLegacyRowDto
                    {
                        lineId = lineId,
                        lineEntityIndex = line.Index,
                        waypointIndex = waypointIndex,
                        anchorObservationKey = observationKey,
                        stationAnchorId = stationAnchorId,
                        stopEntityIndex = stopEntityIndex,
                        buildingEntityIndex = buildingEntityIndex,
                        legacyAverageMinutes = hasLegacy ? RoundMinutes(legacyObservation.AverageFrames) : 0f,
                        legacySampleCount = hasLegacy ? legacyObservation.SampleCount : 0,
                        anchorAverageMinutes = anchorAverageMinutes,
                        anchorSampleCount = anchorSampleCount,
                        mappingStatus = mappingStatus
                    });
                }
            }

            StationAnchorGroupDto[] anchorGroups = groupObservationKeys
                .Select((observationKey, index) =>
                {
                    m_ObsQuery.TryStationDwell(observationKey, out StationDwellObservation observation);
                    return new StationAnchorGroupDto
                    {
                        anchorObservationKey = observationKey,
                        stationAnchorId = groupStationAnchorIds[index],
                        name = groupNames[index] ?? string.Empty,
                        buildingEntityIndex = groupBuildingEntityIndices[index],
                        stopEntityIndices = groupStopEntityIndices[index].OrderBy(value => value).ToArray(),
                        lineIds = groupLineIds[index].OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        waypointIndices = groupWaypointIndices[index].ToArray(),
                        legacyKeys = groupLegacyKeys[index].ToArray(),
                        anchorAverageMinutes = observation.SampleCount > 0 ? RoundMinutes(observation.AverageFrames) : 0f,
                        anchorSampleCount = observation.SampleCount
                    };
                })
                .OrderBy(group => group.anchorObservationKey, StringComparer.Ordinal)
                .ToArray();

            return new StationAnchorObservationDiagnosticsDto
            {
                summary = new StationAnchorObservationSummaryDto
                {
                    generatedAtFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u,
                    lineCount = lineCount,
                    stopWaypointCount = stopWaypointCount,
                    anchorResolvedCount = anchorResolvedCount,
                    anchorMissingCount = anchorMissingCount,
                    uniqueAnchorCount = anchorGroups.Length,
                    duplicateAnchorOccurrenceCount = anchorGroups.Sum(group => math.max(0, group.legacyKeys.Length - 1))
                },
                stopDwell = new StationAnchorStopDwellSummaryDto
                {
                    legacyObservationCount = m_ObsQuery.WaypointDwellCount,
                    anchorObservationCount = m_ObsQuery.StationDwellCount,
                    legacySampleCount = CountLegacySamples(),
                    anchorSampleCount = CountAnchorSamples(),
                    anchorMissingWriteCount = m_GetTotalAnchorMissing(),
                    anchorRejectedOriginOrTerminalCount = m_GetTotalAnchorRejectedOriginOrTerminal(),
                    suspiciousOriginOrTerminalCount = m_GetTotalSuspiciousOriginOrTerminal(),
                    suspiciousLongDwellCount = m_GetTotalSuspiciousLongDwell()
                },
                persistence = new StationAnchorPersistenceSummaryDto
                {
                    legacyBufferCount = CountBuffer<DwellObservationElement>(),
                    legacyRestoredCount = m_GetLegacyRestoredCount(),
                    anchorBufferCount = CountBuffer<StationDwellObservationElement>(),
                    anchorRestoredCount = m_GetAnchorRestoredCount(),
                    legacyPreserved = true
                },
                anchorGroups = anchorGroups,
                legacyRows = legacyRows.ToArray()
            };
        }

        private int CountLegacySamples()
        {
            int total = 0;
            foreach (DwellObservation item in m_ObsQuery.WaypointDwells)
                total += math.max(0, item.SampleCount);
            return total;
        }

        private int CountAnchorSamples()
        {
            int total = 0;
            foreach (StationDwellObservation item in m_ObsQuery.StationDwells)
                total += math.max(0, item.SampleCount);
            return total;
        }

        private int CountBuffer<T>() where T : unmanaged, IBufferElementData
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !m_EntityManager.HasBuffer<T>(city))
                return 0;

            return m_EntityManager.GetBuffer<T>(city, true).Length;
        }

        private float RoundMinutes(float frames)
        {
            return frames > 0f
                ? (float)Math.Round(frames / (float)m_FramesPerMinute, 2)
                : 0f;
        }
    }
}
