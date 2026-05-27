using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        [DataContract]
        public sealed class StationAnchorObservationDiagnosticsDto
        {
            [DataMember] public StationAnchorObservationSummaryDto summary;
            [DataMember] public StationAnchorStopDwellSummaryDto stopDwell;
            [DataMember] public StationAnchorPersistenceSummaryDto persistence;
            [DataMember] public StationAnchorGroupDto[] anchorGroups;
            [DataMember] public StationAnchorLegacyRowDto[] legacyRows;
        }

        [DataContract]
        public sealed class StationAnchorObservationSummaryDto
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
        public sealed class StationAnchorStopDwellSummaryDto
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
        public sealed class StationAnchorPersistenceSummaryDto
        {
            [DataMember] public int legacyBufferCount;
            [DataMember] public int legacyRestoredCount;
            [DataMember] public int anchorBufferCount;
            [DataMember] public int anchorRestoredCount;
            [DataMember] public bool legacyPreserved;
        }

        [DataContract]
        public sealed class StationAnchorGroupDto
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
        public sealed class StationAnchorLegacyRowDto
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

        private sealed class StationAnchorGroupBuilder
        {
            public string ObservationKey = string.Empty;
            public string StationAnchorId = string.Empty;
            public string Name = string.Empty;
            public int BuildingEntityIndex = -1;
            public readonly HashSet<int> StopEntityIndices = new HashSet<int>();
            public readonly HashSet<string> LineIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<int> WaypointIndices = new List<int>();
            public readonly List<string> LegacyKeys = new List<string>();
        }

        public void RequestDumpStationAnchorObservationDiagnostics()
        {
            try
            {
                StationAnchorObservationDiagnosticsDto diagnostics = BuildStationAnchorObservationDiagnostics();
                string json = DispatchWorkbenchJson.Serialize(diagnostics);
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
                log.Info("[StationAnchorDiagDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                log.Info("[StationAnchorDiagDump] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private StationAnchorObservationDiagnosticsDto BuildStationAnchorObservationDiagnostics()
        {
            List<StationAnchorLegacyRowDto> legacyRows = new List<StationAnchorLegacyRowDto>();
            Dictionary<string, StationAnchorGroupBuilder> groups =
                new Dictionary<string, StationAnchorGroupBuilder>(StringComparer.Ordinal);
            int lineCount = 0;
            int stopWaypointCount = 0;
            int anchorResolvedCount = 0;
            int anchorMissingCount = 0;

            var lines = m_LineQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    Entity line = lines[lineIndex];
                    if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                        continue;

                    lineCount++;
                    string lineId = GetWorkbenchLineId(line);
                    DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                    for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                    {
                        Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
                        if (stopEntity == Entity.Null)
                            continue;

                        stopWaypointCount++;
                        ulong legacyKey = MakeLineWaypointStopObservationKey(line, waypointIndex);
                        bool hasLegacy = m_WaypointStopDwellObservations.TryGetValue(legacyKey, out StopDwellObservation legacyObservation)
                            && legacyObservation.SampleCount > 0
                            && legacyObservation.AverageFrames > 0f;

                        string mappingStatus = "missing-anchor";
                        string observationKey = string.Empty;
                        string stationAnchorId = string.Empty;
                        int buildingEntityIndex = -1;
                        int stopEntityIndex = stopEntity.Index;
                        float anchorAverageMinutes = 0f;
                        int anchorSampleCount = 0;
                        if (TryResolveStationStopDwellAnchor(line, waypointIndex, out StationStopDwellAnchor anchor))
                        {
                            stationAnchorId = anchor.StationAnchorId;
                            buildingEntityIndex = anchor.BuildingEntity == Entity.Null ? -1 : anchor.BuildingEntity.Index;
                            anchorResolvedCount++;
                            mappingStatus = hasLegacy ? "mapped" : "mapped-no-legacy";
                            observationKey = MakeStationStopDwellObservationKey(line, stationAnchorId);

                            if (m_StationStopDwellObservations.TryGetValue(observationKey, out StationStopDwellObservation anchorObservation)
                                && anchorObservation.SampleCount > 0
                                && anchorObservation.AverageFrames > 0f)
                            {
                                anchorAverageMinutes = RoundStationAnchorMinutes(anchorObservation.AverageFrames);
                                anchorSampleCount = anchorObservation.SampleCount;
                            }

                            if (!groups.TryGetValue(observationKey, out StationAnchorGroupBuilder group))
                            {
                                group = new StationAnchorGroupBuilder
                                {
                                    ObservationKey = observationKey,
                                    StationAnchorId = stationAnchorId,
                                    Name = ResolveWorkbenchStationName(stopEntity),
                                    BuildingEntityIndex = buildingEntityIndex
                                };
                                groups[observationKey] = group;
                            }
                            group.StopEntityIndices.Add(stopEntityIndex);
                            if (!string.IsNullOrEmpty(lineId))
                                group.LineIds.Add(lineId);
                            group.WaypointIndices.Add(waypointIndex);
                            group.LegacyKeys.Add(line.Index.ToString() + ":" + waypointIndex.ToString());
                            if (group.BuildingEntityIndex < 0 && buildingEntityIndex >= 0)
                                group.BuildingEntityIndex = buildingEntityIndex;
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
                            legacyAverageMinutes = hasLegacy ? RoundStationAnchorMinutes(legacyObservation.AverageFrames) : 0f,
                            legacySampleCount = hasLegacy ? legacyObservation.SampleCount : 0,
                            anchorAverageMinutes = anchorAverageMinutes,
                            anchorSampleCount = anchorSampleCount,
                            mappingStatus = mappingStatus
                        });
                    }
                }
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }

            StationAnchorGroupDto[] anchorGroups = groups.Values
                .OrderBy(group => group.ObservationKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    m_StationStopDwellObservations.TryGetValue(group.ObservationKey, out StationStopDwellObservation observation);
                    return new StationAnchorGroupDto
                    {
                        anchorObservationKey = group.ObservationKey,
                        stationAnchorId = group.StationAnchorId,
                        name = group.Name ?? string.Empty,
                        buildingEntityIndex = group.BuildingEntityIndex,
                        stopEntityIndices = group.StopEntityIndices.OrderBy(value => value).ToArray(),
                        lineIds = group.LineIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        waypointIndices = group.WaypointIndices.ToArray(),
                        legacyKeys = group.LegacyKeys.ToArray(),
                        anchorAverageMinutes = observation.SampleCount > 0 ? RoundStationAnchorMinutes(observation.AverageFrames) : 0f,
                        anchorSampleCount = observation.SampleCount
                    };
                })
                .ToArray();

            int duplicateAnchorOccurrenceCount = anchorGroups.Sum(group => math.max(0, group.legacyKeys.Length - 1));
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
                    duplicateAnchorOccurrenceCount = duplicateAnchorOccurrenceCount
                },
                stopDwell = new StationAnchorStopDwellSummaryDto
                {
                    legacyObservationCount = m_WaypointStopDwellObservations.Count,
                    anchorObservationCount = m_StationStopDwellObservations.Count,
                    legacySampleCount = m_WaypointStopDwellObservations.Values.Sum(item => math.max(0, item.SampleCount)),
                    anchorSampleCount = m_StationStopDwellObservations.Values.Sum(item => math.max(0, item.SampleCount)),
                    anchorMissingWriteCount = m_StationAnchorDiagTotalAnchorMissing,
                    anchorRejectedOriginOrTerminalCount = m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal,
                    suspiciousOriginOrTerminalCount = m_StationAnchorDiagTotalSuspiciousOriginOrTerminal,
                    suspiciousLongDwellCount = m_StationAnchorDiagTotalSuspiciousLongDwell
                },
                persistence = new StationAnchorPersistenceSummaryDto
                {
                    legacyBufferCount = CountStationAnchorDiagnosticBuffer<StopDwellObservationElement>(),
                    legacyRestoredCount = m_LastStationStopDwellLegacyRestoredCount,
                    anchorBufferCount = CountStationAnchorDiagnosticBuffer<StationStopDwellObservationElement>(),
                    anchorRestoredCount = m_LastStationStopDwellAnchorRestoredCount,
                    legacyPreserved = true
                },
                anchorGroups = anchorGroups,
                legacyRows = legacyRows.ToArray()
            };
        }

        private static float RoundStationAnchorMinutes(float frames)
        {
            return frames > 0f
                ? (float)Math.Round(frames / (float)SIM_FRAMES_PER_MINUTE, 2)
                : 0f;
        }

        private int CountStationAnchorDiagnosticBuffer<T>() where T : unmanaged, IBufferElementData
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<T>(city))
                return 0;

            return EntityManager.GetBuffer<T>(city, true).Length;
        }
    }
}
