using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Game.Common;
using Game.Objects;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        [DataContract]
        public class DispatchPlannerExportSnapshot
        {
            [DataMember]
            public string version;
            [DataMember]
            public uint generatedAtFrame;
            [DataMember]
            public DispatchPlannerLineDto[] lines;
            [DataMember]
            public DispatchPlannerStationDto[] stations;
            [DataMember]
            public DispatchPlannerSegmentDto[] segments;
            [DataMember]
            public DispatchPlannerBypassStationDto[] configuredBypassStations;
            [DataMember]
            public DispatchPlannerBypassStationDto[] candidateBypassStations;
            [DataMember]
            public DispatchPlannerTrackScenarioDto currentTrackScenario;
            [DataMember]
            public DispatchPlannerObservationSummaryDto observations;
            [DataMember]
            public DispatchPlannerRuntimeParamsDto runtimeParams;
            [DataMember]
            public DispatchPlannerDraftDto[] drafts;
        }

        [DataContract]
        public class DispatchPlannerLineDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public int entityIndex;
            [DataMember]
            public string name;
            [DataMember]
            public string kind;
            [DataMember]
            public string configuredKind;
            [DataMember]
            public string transportType;
            [DataMember]
            public int routeNumber;
            [DataMember]
            public int stationCount;
            [DataMember]
            public string color;
            [DataMember]
            public string originStationId;
            [DataMember]
            public string originStationName;
            [DataMember]
            public int originHoldLimitMinutes;
            [DataMember]
            public int maxStationDwellMinutes;
            [DataMember]
            public string allowedDepotId;
            [DataMember]
            public bool hasTimeProfile;
            [DataMember]
            public float estimatedLoopMinutes;
        }

        [DataContract]
        public class DispatchPlannerStationDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string workbenchStationId;
            [DataMember]
            public string lineId;
            [DataMember]
            public string name;
            [DataMember]
            public int order;
            [DataMember]
            public int waypointIndex;
            [DataMember]
            public int trackAtomIndex;
            [DataMember]
            public int stopEntityIndex;
            [DataMember]
            public int buildingEntityIndex;
            [DataMember]
            public float distanceMeters;
            [DataMember]
            public float positionX;
            [DataMember]
            public float positionY;
            [DataMember]
            public float positionZ;
            [DataMember]
            public bool canConfigureBypass;
            [DataMember]
            public bool isConfiguredBypass;
            [DataMember]
            public float profileDwellMinutes;
            [DataMember]
            public float observedDwellMinutes;
            [DataMember]
            public int observedDwellSampleCount;
            [DataMember]
            public string dwellSource;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerSegmentDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public string fromStationId;
            [DataMember]
            public string toStationId;
            [DataMember]
            public int fromOrder;
            [DataMember]
            public int toOrder;
            [DataMember]
            public int fromWaypointIndex;
            [DataMember]
            public int toWaypointIndex;
            [DataMember]
            public float distanceMeters;
            [DataMember]
            public float profileMinutes;
            [DataMember]
            public float estimatedMinutes;
            [DataMember]
            public string source;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerBypassStationDto
        {
            [DataMember]
            public string stationId;
            [DataMember]
            public string workbenchStationId;
            [DataMember]
            public string lineId;
            [DataMember]
            public string name;
            [DataMember]
            public int order;
            [DataMember]
            public int buildingEntityIndex;
            [DataMember]
            public bool isConfigured;
            [DataMember]
            public bool isVirtualCandidate;
            [DataMember]
            public string reason;
        }

        [DataContract]
        public class DispatchPlannerTrackScenarioDto
        {
            [DataMember]
            public string scenarioId;
            [DataMember]
            public string scenarioType;
            [DataMember]
            public DispatchPlannerLineTrackDto[] lines;
            [DataMember]
            public DispatchPlannerSharedCorridorDto[] sharedCorridors;
            [DataMember]
            public int configuredBypassStationCount;
            [DataMember]
            public int candidateBypassStationCount;
            [DataMember]
            public int sharedCorridorCount;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerLineTrackDto
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public bool available;
            [DataMember]
            public string unavailableReason;
            [DataMember]
            public string chainSignature;
            [DataMember]
            public int trackAtomCount;
            [DataMember]
            public int controlPointCount;
            [DataMember]
            public int sharedRunCount;
            [DataMember]
            public int protectedIntervalCount;
            [DataMember]
            public int protectedSharedIntervalCount;
            [DataMember]
            public string executionMode;
            [DataMember]
            public DispatchPlannerProtectedIntervalDto[] protectedIntervals;
            [DataMember]
            public DispatchPlannerTraversalSliceDto[] traversalSlices;
        }

        [DataContract]
        public class DispatchPlannerProtectedIntervalDto
        {
            [DataMember]
            public int intervalIndex;
            [DataMember]
            public string fromStationId;
            [DataMember]
            public string toStationId;
            [DataMember]
            public int fromBuildingEntityIndex;
            [DataMember]
            public int toBuildingEntityIndex;
            [DataMember]
            public int startControlPointIndex;
            [DataMember]
            public int endControlPointIndex;
            [DataMember]
            public int startAtomIndex;
            [DataMember]
            public int endAtomIndexExclusive;
            [DataMember]
            public float baseMinutes;
            [DataMember]
            public int sharedSegmentCount;
            [DataMember]
            public int maxSharedLineCount;
            [DataMember]
            public bool hasMirroredContext;
            [DataMember]
            public float minEntryOffsetMinutes;
            [DataMember]
            public float maxClearOffsetMinutes;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerTraversalSliceDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public int sliceIndex;
            [DataMember]
            public int startAtomIndex;
            [DataMember]
            public int endAtomIndexExclusive;
            [DataMember]
            public int physicalLaneCount;
            [DataMember]
            public float modelRunMinutes;
            [DataMember]
            public float observedAverageMinutes;
            [DataMember]
            public float observedFastMinutes;
            [DataMember]
            public int observedSampleCount;
            [DataMember]
            public uint lastObservedFrame;
            [DataMember]
            public string source;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerSharedCorridorDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public string otherLineId;
            [DataMember]
            public int lineStartAtomIndex;
            [DataMember]
            public int lineEndAtomIndexExclusive;
            [DataMember]
            public int otherStartAtomIndex;
            [DataMember]
            public int otherEndAtomIndexExclusive;
            [DataMember]
            public string lineStartStationId;
            [DataMember]
            public string lineEndStationId;
            [DataMember]
            public string otherStartStationId;
            [DataMember]
            public string otherEndStationId;
            [DataMember]
            public int lineSharedSliceCount;
            [DataMember]
            public int otherSharedSliceCount;
            [DataMember]
            public int lineBridgedGapAtoms;
            [DataMember]
            public int otherBridgedGapAtoms;
            [DataMember]
            public int physicalOverlap;
            [DataMember]
            public int orderedRun;
            [DataMember]
            public bool hasMirroredContext;
            [DataMember]
            public int maxSharedLineCount;
            [DataMember]
            public string traversalRelation;
            [DataMember]
            public bool hasCanonicalDirection;
            [DataMember]
            public bool lineAlongCanonical;
            [DataMember]
            public bool otherAlongCanonical;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerObservationSummaryDto
        {
            [DataMember]
            public int stopDwellObservationCount;
            [DataMember]
            public int stopDwellSampleCount;
            [DataMember]
            public int traversalSliceObservationCount;
            [DataMember]
            public int traversalSliceSampleCount;
            [DataMember]
            public DispatchPlannerStationDwellObservationDto[] stopDwell;
            [DataMember]
            public DispatchPlannerTraversalSliceDto[] traversalSlices;
        }

        [DataContract]
        public class DispatchPlannerStationDwellObservationDto
        {
            [DataMember]
            public string stationId;
            [DataMember]
            public string lineId;
            [DataMember]
            public int waypointIndex;
            [DataMember]
            public float averageMinutes;
            [DataMember]
            public int sampleCount;
            [DataMember]
            public string source;
            [DataMember]
            public float confidence;
        }

        [DataContract]
        public class DispatchPlannerRuntimeParamsDto
        {
            [DataMember]
            public double simFramesPerMinute;
            [DataMember]
            public int defaultOriginHoldLimitMinutes;
            [DataMember]
            public int defaultMaxStationDwellMinutes;
            [DataMember]
            public float trackModelEntryClearSafetyGapMinutes;
            [DataMember]
            public float localBypassExitReleaseAtoms;
            [DataMember]
            public float localBypassTrainTailClearAtoms;
            [DataMember]
            public int minStrongProtectedIntervalOverlapAtoms;
            [DataMember]
            public int minStrongProtectedIntervalOrderedRun;
            [DataMember]
            public string compatibilityMode;
        }

        [DataContract]
        public class DispatchPlannerDraftDto
        {
            [DataMember]
            public string lineKey;
            [DataMember]
            public string selectedLineId;
            [DataMember]
            public string selectedEditLine;
            [DataMember]
            public DispatchWorkbenchMergedView mergedView;
            [DataMember]
            public DispatchWorkbenchManualRowDto[] manualRows;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public DispatchWorkbenchAutoRuleDto[] autoRules;
            [DataMember]
            public DispatchWorkbenchTripDto[] trips;
        }

        private sealed class PlannerStationRecord
        {
            public DispatchPlannerStationDto Dto;
            public Entity LineEntity;
            public Entity StopEntity;
            public Entity BuildingEntity;
            public int WaypointIndex;
            public int TrackAtomIndex;
            public float3 Position;
        }

        public string ExportPlannerInputJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildPlannerExportSnapshot());
        }

        public void RequestDumpPlannerInputSnapshot()
        {
            try
            {
                string json = ExportPlannerInputJson();
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-planner-input-latest.json");
                File.WriteAllText(filePath, json);
                Mod.log.Info("[PlannerInputDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[PlannerInputDump] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private DispatchPlannerExportSnapshot BuildPlannerExportSnapshot()
        {
            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            List<DispatchPlannerLineDto> lines = new List<DispatchPlannerLineDto>();
            List<DispatchPlannerStationDto> stations = new List<DispatchPlannerStationDto>();
            List<DispatchPlannerSegmentDto> segments = new List<DispatchPlannerSegmentDto>();
            Dictionary<string, List<PlannerStationRecord>> stationRecordsByLine =
                new Dictionary<string, List<PlannerStationRecord>>(StringComparer.Ordinal);
            List<PlannerStationRecord> allStationRecords = new List<PlannerStationRecord>();

            for (int i = 0; i < runtimeLines.Count; i++)
            {
                WorkbenchLineRuntime runtime = runtimeLines[i];
                if (runtime.Entity == Entity.Null
                    || !EntityManager.Exists(runtime.Entity)
                    || !EntityManager.HasBuffer<RouteWaypoint>(runtime.Entity))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(runtime.Entity, true);
                bool hasProfile = TryGetLineTimeProfile(runtime.Entity, waypoints, out LineTimeProfileHeader profile);
                List<PlannerStationRecord> lineStations = BuildPlannerStationRecords(runtime, waypoints, hasProfile, profile);
                stationRecordsByLine[runtime.Id] = lineStations;
                for (int stationIndex = 0; stationIndex < lineStations.Count; stationIndex++)
                {
                    stations.Add(lineStations[stationIndex].Dto);
                    allStationRecords.Add(lineStations[stationIndex]);
                }

                segments.AddRange(BuildPlannerSegments(runtime, waypoints.Length, lineStations, hasProfile, profile));

                lines.Add(new DispatchPlannerLineDto
                {
                    id = runtime.Id,
                    entityIndex = runtime.Entity.Index,
                    name = runtime.Name ?? string.Empty,
                    kind = runtime.Kind ?? "local",
                    configuredKind = GetWorkbenchConfiguredLineServiceKind(runtime.Entity),
                    transportType = runtime.TransportType ?? string.Empty,
                    routeNumber = runtime.RouteNumber == int.MaxValue ? -1 : runtime.RouteNumber,
                    stationCount = lineStations.Count,
                    color = runtime.Color ?? string.Empty,
                    originStationId = runtime.OriginStationId ?? string.Empty,
                    originStationName = runtime.OriginStationName ?? string.Empty,
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(runtime.Entity),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(runtime.Entity),
                    allowedDepotId = GetWorkbenchAllowedDepotId(runtime.Id),
                    hasTimeProfile = hasProfile,
                    estimatedLoopMinutes = hasProfile ? RoundPlannerMinutes(profile.m_BaseLoopFrames) : 0f
                });
            }

            List<DispatchPlannerTraversalSliceDto> traversalSlices;
            DispatchPlannerTrackScenarioDto currentTrackScenario = BuildPlannerTrackScenario(
                runtimeLines,
                stationRecordsByLine,
                out traversalSlices);

            DispatchPlannerObservationSummaryDto observations = BuildPlannerObservationSummary(
                allStationRecords,
                traversalSlices);
            DispatchPlannerBypassStationDto[] configuredBypassStations = BuildPlannerBypassStations(
                allStationRecords,
                configuredOnly: true);
            DispatchPlannerBypassStationDto[] candidateBypassStations = BuildPlannerBypassStations(
                allStationRecords,
                configuredOnly: false);
            currentTrackScenario.configuredBypassStationCount = configuredBypassStations.Length;
            currentTrackScenario.candidateBypassStationCount = candidateBypassStations.Length;

            return new DispatchPlannerExportSnapshot
            {
                version = "planner-input-v2",
                generatedAtFrame = m_SimulationSystem.frameIndex,
                lines = lines.ToArray(),
                stations = stations.ToArray(),
                segments = segments.ToArray(),
                configuredBypassStations = configuredBypassStations,
                candidateBypassStations = candidateBypassStations,
                currentTrackScenario = currentTrackScenario,
                observations = observations,
                runtimeParams = BuildPlannerRuntimeParams(),
                drafts = BuildPlannerDrafts(runtimeLines)
            };
        }

        private List<PlannerStationRecord> BuildPlannerStationRecords(
            WorkbenchLineRuntime runtime,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasProfile,
            LineTimeProfileHeader profile)
        {
            List<PlannerStationRecord> stations = new List<PlannerStationRecord>();
            float cumulativeDistance = 0f;
            float3 previousPosition = float3.zero;
            bool hasPrevious = false;
            HashSet<Entity> seenStopEntities = new HashSet<Entity>();

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
                if (stopEntity == Entity.Null || !seenStopEntities.Add(stopEntity))
                {
                    continue;
                }

                if (!TryResolvePlannerWaypointPosition(waypoint, out float3 position))
                {
                    continue;
                }

                if (hasPrevious)
                {
                    cumulativeDistance += math.distance(previousPosition, position);
                }

                previousPosition = position;
                hasPrevious = true;

                Entity building = FindTransportStationFromStop(stopEntity);
                if (building == Entity.Null)
                {
                    building = ResolvePassingStationBuilding(stopEntity);
                }

                string name = ResolveWorkbenchStationName(stopEntity);
                if (string.IsNullOrEmpty(name))
                {
                    name = "Stop " + (stations.Count + 1).ToString();
                }

                string workbenchStationId = CreateWorkbenchStationId(stations.Count);
                string stationId = CreatePlannerStationId(runtime.Id, stations.Count);
                float profileDwellMinutes = hasProfile ? RoundPlannerMinutes(ProfileStopFrames(profile, i)) : 0f;
                bool hasObservedDwell = m_WaypointStopDwellObservations.TryGetValue(
                    MakeLineWaypointStopObservationKey(runtime.Entity, i),
                    out StopDwellObservation dwellObservation)
                    && dwellObservation.SampleCount > 0
                    && dwellObservation.AverageFrames > 0f;

                DispatchPlannerStationDto dto = new DispatchPlannerStationDto
                {
                    id = stationId,
                    workbenchStationId = workbenchStationId,
                    lineId = runtime.Id,
                    name = name,
                    order = stations.Count,
                    waypointIndex = i,
                    trackAtomIndex = -1,
                    stopEntityIndex = stopEntity.Index,
                    buildingEntityIndex = building == Entity.Null ? -1 : building.Index,
                    distanceMeters = (float)Math.Round(cumulativeDistance, 1),
                    positionX = (float)Math.Round(position.x, 1),
                    positionY = (float)Math.Round(position.y, 1),
                    positionZ = (float)Math.Round(position.z, 1),
                    canConfigureBypass = building != Entity.Null && CanConfigureBypassStation(building),
                    isConfiguredBypass = building != Entity.Null && IsBypassStation(building),
                    profileDwellMinutes = profileDwellMinutes,
                    observedDwellMinutes = hasObservedDwell ? RoundPlannerMinutes(dwellObservation.AverageFrames) : 0f,
                    observedDwellSampleCount = hasObservedDwell ? dwellObservation.SampleCount : 0,
                    dwellSource = hasObservedDwell ? "observed" : hasProfile ? "profile" : "unavailable",
                    confidence = hasObservedDwell ? ComputePlannerSampleConfidence(dwellObservation.SampleCount) : hasProfile ? 0.55f : 0.2f
                };

                stations.Add(new PlannerStationRecord
                {
                    Dto = dto,
                    LineEntity = runtime.Entity,
                    StopEntity = stopEntity,
                    BuildingEntity = building,
                    WaypointIndex = i,
                    TrackAtomIndex = -1,
                    Position = position
                });
            }

            return stations;
        }

        private List<DispatchPlannerSegmentDto> BuildPlannerSegments(
            WorkbenchLineRuntime runtime,
            int waypointCount,
            List<PlannerStationRecord> stations,
            bool hasProfile,
            LineTimeProfileHeader profile)
        {
            List<DispatchPlannerSegmentDto> segments = new List<DispatchPlannerSegmentDto>();
            for (int i = 1; i < stations.Count; i++)
            {
                PlannerStationRecord previous = stations[i - 1];
                PlannerStationRecord next = stations[i];
                float distanceMeters = math.distance(previous.Position, next.Position);
                float profileMinutes = hasProfile
                    ? ComputePlannerProfileRunMinutes(profile, previous.WaypointIndex, next.WaypointIndex, waypointCount)
                    : 0f;
                float estimatedMinutes = profileMinutes > 0f
                    ? profileMinutes
                    : math.max(2f, distanceMeters / 900f);

                segments.Add(new DispatchPlannerSegmentDto
                {
                    id = runtime.Id + ":segment-" + (i - 1).ToString() + "-" + i.ToString(),
                    lineId = runtime.Id,
                    fromStationId = previous.Dto.id,
                    toStationId = next.Dto.id,
                    fromOrder = previous.Dto.order,
                    toOrder = next.Dto.order,
                    fromWaypointIndex = previous.WaypointIndex,
                    toWaypointIndex = next.WaypointIndex,
                    distanceMeters = (float)Math.Round(distanceMeters, 1),
                    profileMinutes = profileMinutes,
                    estimatedMinutes = (float)Math.Round(estimatedMinutes, 2),
                    source = profileMinutes > 0f ? "profile" : "distanceFallback",
                    confidence = profileMinutes > 0f ? 0.7f : 0.25f
                });
            }

            return segments;
        }

        private DispatchPlannerTrackScenarioDto BuildPlannerTrackScenario(
            List<WorkbenchLineRuntime> runtimeLines,
            Dictionary<string, List<PlannerStationRecord>> stationRecordsByLine,
            out List<DispatchPlannerTraversalSliceDto> traversalSlices)
        {
            List<DispatchPlannerLineTrackDto> lineTracks = new List<DispatchPlannerLineTrackDto>();
            List<DispatchPlannerSharedCorridorDto> sharedCorridors = new List<DispatchPlannerSharedCorridorDto>();
            traversalSlices = new List<DispatchPlannerTraversalSliceDto>();
            Dictionary<string, LineTrackChain> chainByLineId = new Dictionary<string, LineTrackChain>(StringComparer.Ordinal);

            for (int i = 0; i < runtimeLines.Count; i++)
            {
                WorkbenchLineRuntime runtime = runtimeLines[i];
                if (runtime.Entity == Entity.Null
                    || !EntityManager.Exists(runtime.Entity)
                    || !EntityManager.HasBuffer<RouteWaypoint>(runtime.Entity))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(runtime.Entity, true);
                List<PlannerStationRecord> stationRecords = stationRecordsByLine.TryGetValue(runtime.Id, out List<PlannerStationRecord> records)
                    ? records
                    : new List<PlannerStationRecord>();
                DispatchPlannerLineTrackDto lineTrack = BuildPlannerLineTrack(runtime, waypoints, stationRecords);
                lineTracks.Add(lineTrack);
                if (lineTrack.available
                    && TryGetLineTrackChain(runtime.Entity, waypoints, out LineTrackChain chain)
                    && chain != null)
                {
                    chainByLineId[runtime.Id] = chain;
                }
                if (lineTrack.traversalSlices != null)
                {
                    traversalSlices.AddRange(lineTrack.traversalSlices);
                }
            }

            sharedCorridors.AddRange(BuildPlannerSharedCorridors(chainByLineId, stationRecordsByLine));

            return new DispatchPlannerTrackScenarioDto
            {
                scenarioId = "current-configured",
                scenarioType = "configured",
                lines = lineTracks.ToArray(),
                sharedCorridors = sharedCorridors.ToArray(),
                sharedCorridorCount = sharedCorridors.Count,
                confidence = lineTracks.Count > 0 ? 0.65f : 0f
            };
        }

        private DispatchPlannerLineTrackDto BuildPlannerLineTrack(
            WorkbenchLineRuntime runtime,
            DynamicBuffer<RouteWaypoint> waypoints,
            List<PlannerStationRecord> stationRecords)
        {
            if (!TryGetLineTrackChain(runtime.Entity, waypoints, out LineTrackChain chain)
                || chain == null)
            {
                return new DispatchPlannerLineTrackDto
                {
                    lineId = runtime.Id,
                    available = false,
                    unavailableReason = "no-track-chain",
                    protectedIntervals = Array.Empty<DispatchPlannerProtectedIntervalDto>(),
                    traversalSlices = Array.Empty<DispatchPlannerTraversalSliceDto>()
                };
            }

            EnsureTrackChainBypassPipelineReady(chain);
            EnsureLineBypassExecutionModeReady(chain, waypoints);
            PopulatePlannerStationTrackAtomIndices(chain, stationRecords);

            return new DispatchPlannerLineTrackDto
            {
                lineId = runtime.Id,
                available = true,
                unavailableReason = string.Empty,
                chainSignature = chain.Signature.ToString(),
                trackAtomCount = chain.TrackAtoms.Count,
                controlPointCount = chain.ControlPoints.Count,
                sharedRunCount = chain.SharedRuns.Count,
                protectedIntervalCount = chain.BypassProtectedIntervals.Count,
                protectedSharedIntervalCount = chain.ProtectedSharedIntervals.Count,
                executionMode = chain.ExecutionMode.ToString(),
                protectedIntervals = BuildPlannerProtectedIntervals(runtime.Id, chain, stationRecords),
                traversalSlices = BuildPlannerTraversalSlices(runtime.Id, runtime.Entity, chain)
            };
        }

        private void PopulatePlannerStationTrackAtomIndices(
            LineTrackChain chain,
            List<PlannerStationRecord> stationRecords)
        {
            if (chain == null || stationRecords == null)
                return;

            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                int trackAtomIndex = -1;
                if (station.WaypointIndex >= 0
                    && station.WaypointIndex < chain.SegmentRanges.Count)
                {
                    trackAtomIndex = chain.SegmentRanges[station.WaypointIndex].StartAtomIndex;
                }

                station.TrackAtomIndex = trackAtomIndex;
                station.Dto.trackAtomIndex = trackAtomIndex;
            }
        }

        private DispatchPlannerProtectedIntervalDto[] BuildPlannerProtectedIntervals(
            string lineId,
            LineTrackChain chain,
            List<PlannerStationRecord> stationRecords)
        {
            List<DispatchPlannerProtectedIntervalDto> intervals = new List<DispatchPlannerProtectedIntervalDto>();
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval interval = chain.BypassProtectedIntervals[i];
                ProtectedIntervalSummary summary = i < chain.ProtectedIntervalSummaries.Count
                    ? chain.ProtectedIntervalSummaries[i]
                    : default;
                ControlPointMarker startPoint = interval.StartControlPointIndex >= 0 && interval.StartControlPointIndex < chain.ControlPoints.Count
                    ? chain.ControlPoints[interval.StartControlPointIndex]
                    : default;
                ControlPointMarker endPoint = interval.EndControlPointIndex >= 0 && interval.EndControlPointIndex < chain.ControlPoints.Count
                    ? chain.ControlPoints[interval.EndControlPointIndex]
                    : default;

                intervals.Add(new DispatchPlannerProtectedIntervalDto
                {
                    intervalIndex = i,
                    fromStationId = ResolvePlannerStationIdForBuilding(stationRecords, startPoint.Building),
                    toStationId = ResolvePlannerStationIdForBuilding(stationRecords, endPoint.Building),
                    fromBuildingEntityIndex = startPoint.Building == Entity.Null ? -1 : startPoint.Building.Index,
                    toBuildingEntityIndex = endPoint.Building == Entity.Null ? -1 : endPoint.Building.Index,
                    startControlPointIndex = interval.StartControlPointIndex,
                    endControlPointIndex = interval.EndControlPointIndex,
                    startAtomIndex = interval.StartAtomIndex,
                    endAtomIndexExclusive = interval.EndAtomIndexExclusive,
                    baseMinutes = RoundPlannerMinutes(interval.BaseFrames),
                    sharedSegmentCount = summary.SharedSegmentCount,
                    maxSharedLineCount = summary.MaxSharedLineCount,
                    hasMirroredContext = summary.HasMirroredContext,
                    minEntryOffsetMinutes = RoundPlannerMinutes(summary.MinEntryOffsetFrames),
                    maxClearOffsetMinutes = RoundPlannerMinutes(summary.MaxClearOffsetFrames),
                    confidence = summary.SharedSegmentCount > 0 ? 0.7f : 0.45f
                });
            }

            return intervals.ToArray();
        }

        private DispatchPlannerTraversalSliceDto[] BuildPlannerTraversalSlices(
            string lineId,
            Entity line,
            LineTrackChain chain)
        {
            List<DispatchPlannerTraversalSliceDto> slices = new List<DispatchPlannerTraversalSliceDto>();
            if (chain.TraversalProfile == null || chain.TraversalProfile.RunSlices == null)
            {
                return slices.ToArray();
            }

            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[i];
                bool hasObservation = m_TraversalRunSliceObservations.TryGetValue(
                    MakeTraversalSliceObservationKey(line, slice.SliceIndex),
                    out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;

                slices.Add(new DispatchPlannerTraversalSliceDto
                {
                    id = lineId + ":slice-" + slice.SliceIndex.ToString(),
                    lineId = lineId,
                    sliceIndex = slice.SliceIndex,
                    startAtomIndex = slice.StartAtomIndex,
                    endAtomIndexExclusive = slice.EndAtomIndexExclusive,
                    physicalLaneCount = slice.PhysicalLaneKeys != null ? slice.PhysicalLaneKeys.Length : 0,
                    modelRunMinutes = RoundPlannerMinutes(slice.RunFrames),
                    observedAverageMinutes = hasObservation ? RoundPlannerMinutes(observation.AverageFrames) : 0f,
                    observedFastMinutes = hasObservation ? RoundPlannerMinutes(observation.FastBaselineFrames) : 0f,
                    observedSampleCount = hasObservation ? observation.SampleCount : 0,
                    lastObservedFrame = hasObservation ? observation.LastObservedFrame : 0u,
                    source = hasObservation ? "observed" : "model",
                    confidence = hasObservation ? ComputePlannerSampleConfidence(observation.SampleCount) : 0.45f
                });
            }

            return slices.ToArray();
        }

        private DispatchPlannerSharedCorridorDto[] BuildPlannerSharedCorridors(
            Dictionary<string, LineTrackChain> chainByLineId,
            Dictionary<string, List<PlannerStationRecord>> stationRecordsByLine)
        {
            List<DispatchPlannerSharedCorridorDto> corridors = new List<DispatchPlannerSharedCorridorDto>();
            if (chainByLineId == null || chainByLineId.Count <= 1)
                return corridors.ToArray();

            foreach (KeyValuePair<string, LineTrackChain> leftEntry in chainByLineId)
            {
                foreach (KeyValuePair<string, LineTrackChain> rightEntry in chainByLineId)
                {
                    if (leftEntry.Key == rightEntry.Key)
                        continue;

                    GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(leftEntry.Value, rightEntry.Value);
                    if (snapshot == null || snapshot.Segments == null || snapshot.Segments.Count == 0)
                        continue;

                    List<PlannerStationRecord> leftStations = stationRecordsByLine.TryGetValue(leftEntry.Key, out List<PlannerStationRecord> leftResolved)
                        ? leftResolved
                        : null;
                    List<PlannerStationRecord> rightStations = stationRecordsByLine.TryGetValue(rightEntry.Key, out List<PlannerStationRecord> rightResolved)
                        ? rightResolved
                        : null;

                    for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
                    {
                        GlobalSharedTrunkSegment segment = snapshot.Segments[segmentIndex];
                        corridors.Add(new DispatchPlannerSharedCorridorDto
                        {
                            id = leftEntry.Key + "|" + rightEntry.Key + "|trunk-" + segmentIndex.ToString(),
                            lineId = leftEntry.Key,
                            otherLineId = rightEntry.Key,
                            lineStartAtomIndex = segment.LocalCorridorStartAtomIndex,
                            lineEndAtomIndexExclusive = segment.LocalCorridorEndAtomIndexExclusive,
                            otherStartAtomIndex = segment.ExpressCorridorStartAtomIndex,
                            otherEndAtomIndexExclusive = segment.ExpressCorridorEndAtomIndexExclusive,
                            lineStartStationId = ResolvePlannerStationIdAtOrBeforeAtom(leftStations, segment.LocalCorridorStartAtomIndex),
                            lineEndStationId = ResolvePlannerStationIdAtOrAfterAtom(leftStations, segment.LocalCorridorEndAtomIndexExclusive),
                            otherStartStationId = ResolvePlannerStationIdAtOrBeforeAtom(rightStations, segment.ExpressCorridorStartAtomIndex),
                            otherEndStationId = ResolvePlannerStationIdAtOrAfterAtom(rightStations, segment.ExpressCorridorEndAtomIndexExclusive),
                            lineSharedSliceCount = segment.LocalSharedSliceCount,
                            otherSharedSliceCount = segment.ExpressSharedSliceCount,
                            lineBridgedGapAtoms = segment.LocalBridgedGapAtoms,
                            otherBridgedGapAtoms = segment.ExpressBridgedGapAtoms,
                            physicalOverlap = segment.PhysicalOverlap,
                            orderedRun = segment.OrderedRun,
                            hasMirroredContext = segment.HasMirroredContext,
                            maxSharedLineCount = segment.MaxSharedLineCount,
                            traversalRelation = segment.TraversalRelation.ToString(),
                            hasCanonicalDirection = segment.HasCanonicalDirection,
                            lineAlongCanonical = segment.LocalAlongCanonical,
                            otherAlongCanonical = segment.ExpressAlongCanonical,
                            confidence = ComputePlannerSharedCorridorConfidence(segment)
                        });
                    }
                }
            }

            return corridors.ToArray();
        }

        private DispatchPlannerObservationSummaryDto BuildPlannerObservationSummary(
            List<PlannerStationRecord> stationRecords,
            List<DispatchPlannerTraversalSliceDto> traversalSlices)
        {
            List<DispatchPlannerStationDwellObservationDto> stopDwell = new List<DispatchPlannerStationDwellObservationDto>();
            int stopDwellSampleCount = 0;
            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (!m_WaypointStopDwellObservations.TryGetValue(
                        MakeLineWaypointStopObservationKey(
                            station.LineEntity,
                            station.WaypointIndex),
                        out StopDwellObservation observation)
                    || observation.SampleCount <= 0
                    || !(observation.AverageFrames > 0f))
                {
                    continue;
                }

                stopDwellSampleCount += observation.SampleCount;
                stopDwell.Add(new DispatchPlannerStationDwellObservationDto
                {
                    stationId = station.Dto.id,
                    lineId = station.Dto.lineId,
                    waypointIndex = station.WaypointIndex,
                    averageMinutes = RoundPlannerMinutes(observation.AverageFrames),
                    sampleCount = observation.SampleCount,
                    source = "observed",
                    confidence = ComputePlannerSampleConfidence(observation.SampleCount)
                });
            }

            int traversalSampleCount = 0;
            int traversalObservationCount = 0;
            for (int i = 0; i < traversalSlices.Count; i++)
            {
                if (traversalSlices[i].observedSampleCount <= 0)
                    continue;

                traversalObservationCount++;
                traversalSampleCount += traversalSlices[i].observedSampleCount;
            }

            return new DispatchPlannerObservationSummaryDto
            {
                stopDwellObservationCount = stopDwell.Count,
                stopDwellSampleCount = stopDwellSampleCount,
                traversalSliceObservationCount = traversalObservationCount,
                traversalSliceSampleCount = traversalSampleCount,
                stopDwell = stopDwell.ToArray(),
                traversalSlices = traversalSlices.ToArray()
            };
        }

        private DispatchPlannerBypassStationDto[] BuildPlannerBypassStations(
            List<PlannerStationRecord> stationRecords,
            bool configuredOnly)
        {
            List<DispatchPlannerBypassStationDto> result = new List<DispatchPlannerBypassStationDto>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (!station.Dto.canConfigureBypass)
                    continue;
                if (configuredOnly && !station.Dto.isConfiguredBypass)
                    continue;

                string key = station.Dto.lineId + "|" + station.Dto.buildingEntityIndex.ToString() + "|" + station.Dto.order.ToString();
                if (!seen.Add(key))
                    continue;

                result.Add(new DispatchPlannerBypassStationDto
                {
                    stationId = station.Dto.id,
                    workbenchStationId = station.Dto.workbenchStationId,
                    lineId = station.Dto.lineId,
                    name = station.Dto.name,
                    order = station.Dto.order,
                    buildingEntityIndex = station.Dto.buildingEntityIndex,
                    isConfigured = station.Dto.isConfiguredBypass,
                    isVirtualCandidate = !station.Dto.isConfiguredBypass,
                    reason = station.Dto.isConfiguredBypass ? "configured" : "configurable-station"
                });
            }

            return result.ToArray();
        }

        private DispatchPlannerRuntimeParamsDto BuildPlannerRuntimeParams()
        {
            return new DispatchPlannerRuntimeParamsDto
            {
                simFramesPerMinute = SIM_FRAMES_PER_MINUTE,
                defaultOriginHoldLimitMinutes = DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES,
                defaultMaxStationDwellMinutes = DEFAULT_MAX_STATION_DWELL_MINUTES,
                trackModelEntryClearSafetyGapMinutes = TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES,
                localBypassExitReleaseAtoms = LOCAL_BYPASS_EXIT_RELEASE_ATOMS,
                localBypassTrainTailClearAtoms = LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS,
                minStrongProtectedIntervalOverlapAtoms = MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS,
                minStrongProtectedIntervalOrderedRun = MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN,
                compatibilityMode = "read-only-planner-input"
            };
        }

        private DispatchPlannerDraftDto[] BuildPlannerDrafts(List<WorkbenchLineRuntime> runtimeLines)
        {
            List<DispatchPlannerDraftDto> drafts = new List<DispatchPlannerDraftDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                string preferredLineId = !string.IsNullOrEmpty(draft?.SelectedLineId)
                    ? draft.SelectedLineId
                    : !string.IsNullOrEmpty(draft?.MergedView?.localLineId)
                        ? draft.MergedView.localLineId
                        : entry.Key;
                WorkbenchLineRuntime activeRuntime = runtimeLines != null && runtimeLines.Count > 0
                    ? ResolveActiveWorkbenchLine(runtimeLines, preferredLineId)
                    : null;
                List<DispatchWorkbenchStationDto> workbenchStations = activeRuntime != null
                    ? BuildWorkbenchStations(activeRuntime.Entity)
                    : new List<DispatchWorkbenchStationDto>();
                List<DispatchWorkbenchTripDto> trips = activeRuntime != null
                    ? BuildRealtimeWorkbenchTrips(activeRuntime, workbenchStations, draft)
                    : new List<DispatchWorkbenchTripDto>();
                drafts.Add(new DispatchPlannerDraftDto
                {
                    lineKey = entry.Key,
                    selectedLineId = draft.SelectedLineId ?? string.Empty,
                    selectedEditLine = draft.SelectedEditLine ?? string.Empty,
                    mergedView = draft.MergedView,
                    manualRows = draft.ManualRows != null ? draft.ManualRows.Select(CloneManualRow).ToArray() : Array.Empty<DispatchWorkbenchManualRowDto>(),
                    stagedRows = draft.StagedRows != null ? draft.StagedRows.ToArray() : Array.Empty<DispatchWorkbenchStagedRowDto>(),
                    autoRules = draft.AutoRules != null ? draft.AutoRules.Select(CloneAutoRule).ToArray() : Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                    trips = trips.ToArray()
                });
            }

            return drafts.ToArray();
        }

        private bool TryResolvePlannerWaypointPosition(Entity waypoint, out float3 position)
        {
            Entity positionEntity = waypoint;
            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connected != Entity.Null && EntityManager.HasComponent<Transform>(connected))
                {
                    positionEntity = connected;
                }
            }

            if (!EntityManager.HasComponent<Transform>(positionEntity))
            {
                position = float3.zero;
                return false;
            }

            position = EntityManager.GetComponentData<Transform>(positionEntity).m_Position;
            return true;
        }

        private float ComputePlannerProfileRunMinutes(
            LineTimeProfileHeader profile,
            int fromWaypointIndex,
            int toWaypointIndex,
            int waypointCount)
        {
            if (waypointCount <= 0
                || fromWaypointIndex < 0
                || toWaypointIndex < 0
                || fromWaypointIndex >= waypointCount
                || toWaypointIndex >= waypointCount)
            {
                return 0f;
            }

            float totalFrames = 0f;
            int cursor = fromWaypointIndex;
            int guard = 0;
            while (cursor != toWaypointIndex && guard < waypointCount)
            {
                totalFrames += ProfileSegmentFrames(profile, cursor);
                cursor = (cursor + 1) % waypointCount;
                guard++;
            }

            return totalFrames > 0f ? RoundPlannerMinutes(totalFrames) : 0f;
        }

        private float ProfileSegmentFrames(LineTimeProfileHeader profile, int segmentIndex)
        {
            if (segmentIndex < 0 || segmentIndex >= profile.m_Count)
                return 0f;

            return m_LineTimeProfileSegmentFrames[profile.m_Offset + segmentIndex];
        }

        private float ProfileStopFrames(LineTimeProfileHeader profile, int stopIndex)
        {
            if (stopIndex < 0 || stopIndex >= profile.m_Count)
                return 0f;

            return m_LineTimeProfileStopFrames[profile.m_Offset + stopIndex];
        }

        private static string CreatePlannerStationId(string lineId, int order)
        {
            return (lineId ?? string.Empty) + ":station-" + order.ToString();
        }

        private static float RoundPlannerMinutes(float frames)
        {
            if (!(frames > 0f))
                return 0f;

            return (float)Math.Round(frames / (float)SIM_FRAMES_PER_MINUTE, 2);
        }

        private static float ComputePlannerSampleConfidence(int sampleCount)
        {
            if (sampleCount <= 0)
                return 0.2f;

            return math.min(0.9f, 0.35f + sampleCount * 0.08f);
        }

        private static string ResolvePlannerStationIdForBuilding(
            List<PlannerStationRecord> stationRecords,
            Entity building)
        {
            if (building == Entity.Null || stationRecords == null)
                return string.Empty;

            for (int i = 0; i < stationRecords.Count; i++)
            {
                if (stationRecords[i].BuildingEntity == building)
                    return stationRecords[i].Dto.id;
            }

            return string.Empty;
        }

        private static string ResolvePlannerStationIdAtOrBeforeAtom(
            List<PlannerStationRecord> stationRecords,
            int atomIndex)
        {
            if (stationRecords == null || stationRecords.Count == 0)
                return string.Empty;

            PlannerStationRecord best = null;
            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (station == null || station.TrackAtomIndex < 0 || station.TrackAtomIndex > atomIndex)
                    continue;

                if (best == null || station.TrackAtomIndex >= best.TrackAtomIndex)
                    best = station;
            }

            if (best != null)
                return best.Dto.id;

            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (station == null || station.TrackAtomIndex < 0)
                    continue;
                return station.Dto.id;
            }

            return string.Empty;
        }

        private static string ResolvePlannerStationIdAtOrAfterAtom(
            List<PlannerStationRecord> stationRecords,
            int atomIndexExclusive)
        {
            if (stationRecords == null || stationRecords.Count == 0)
                return string.Empty;

            PlannerStationRecord best = null;
            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (station == null || station.TrackAtomIndex < 0 || station.TrackAtomIndex < atomIndexExclusive)
                    continue;

                if (best == null || station.TrackAtomIndex < best.TrackAtomIndex)
                    best = station;
            }

            if (best != null)
                return best.Dto.id;

            for (int i = stationRecords.Count - 1; i >= 0; i--)
            {
                PlannerStationRecord station = stationRecords[i];
                if (station == null || station.TrackAtomIndex < 0)
                    continue;
                return station.Dto.id;
            }

            return string.Empty;
        }

        private static float ComputePlannerSharedCorridorConfidence(GlobalSharedTrunkSegment segment)
        {
            float confidence = segment.TraversalRelation == SharedTraversalRelation.SameDirection ? 0.75f : 0.45f;
            if (segment.HasMirroredContext)
                confidence -= 0.2f;
            if (segment.OrderedRun >= MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
                confidence += 0.05f;
            if (segment.PhysicalOverlap >= MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS)
                confidence += 0.05f;

            return math.clamp(confidence, 0.2f, 0.9f);
        }
    }
}
