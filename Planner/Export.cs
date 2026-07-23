using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.TrackModel;
using static RapidTransitMod.Dispatch.Workbench.Rows;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerExport
    {
        private readonly PlannerPort m_Port;

        internal PlannerExport(PlannerPort port)
        {
            m_Port = port;
        }

        private ModRuntimeHostSystem R => m_Port.Runtime;
        private Unity.Entities.EntityManager EntityManager => R.EntityManager;
        private TrackModelService m_TrackModel => R.TrackModel;
        private RuntimeFacade m_Bypass => R.Bypass;
        private Game.Simulation.SimulationSystem m_SimulationSystem => R.m_SimulationSystem;
        private RapidTransitMod.Dispatch.Workbench.Bridge m_WorkbenchBridge => R.m_WorkbenchBridge;
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = ModRuntimeHostSystem.LOCAL_BYPASS_EXIT_RELEASE_ATOMS;

        private List<WorkbenchLineRuntime> Lines() => R.Lines();
        private RapidTransitMod.Dispatch.Workbench.Trips Trips() => R.Trips();
        private Drafts DraftStore() => R.DraftStore();
        private WorkbenchLineRuntime ActiveLine(List<WorkbenchLineRuntime> lines, string preferredLineId) => R.ActiveLine(lines, preferredLineId);
        private List<DispatchWorkbenchStationDto> Stations(Entity line) => R.m_Resolve.Stations(line);
        private bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => R.m_LineTimes.Get(line, waypoints, out profile);
        private string GetKind(Entity line) => R.GetKind(line);
        private int GetHold(Entity line) => R.GetHold(line);
        private int GetDwell(Entity line) => R.GetDwell(line);
        private string GetDepotId(string lineId) => R.GetDepotId(lineId);
        private Entity Stop(Entity waypoint) => R.m_Resolve.Stop(waypoint);
        private Entity StationOf(Entity stop) => R.m_Resolve.StationOf(stop);
        private Entity ResolvePassingStationBuilding(Entity entity) => R.m_Resolve.PassingStation(entity);
        private string StationName(Entity stopEntity) => R.m_Resolve.StationName(stopEntity);
        private string StationId(int order) => Catalog.StationId(order);
        private bool IsBypassStationSetting(Entity entity) => R.IsBypassStationSetting(entity);
        private bool TryResolveStationStopDwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor) => R.m_Observation.DwellAnchor(line, waypointIndex, out anchor);
        private string MakeStationDwellObservationKey(Entity line, string stationAnchorId) => R.m_Observation.DwellKey(line, stationAnchorId);
        private string LineId(Entity line) => R.LineStableId(line);

        internal DispatchPlannerExportSnapshot Load(ModeScope scope)
        {
            return Build(scope);
        }

        internal void Dump()
        {
            m_Port.Dump(this);
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

        internal DispatchPlannerExportSnapshot Build(ModeScope scope)
        {
            scope.EnsureSupportedWorkbenchMode();
            R.LoadWorkbench();
            R.LoadApplied();
            List<WorkbenchLineRuntime> runtimeLines = Lines()
                .Where(line => MatchesScope(line, scope))
                .ToList();
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

                DispatchPlannerOutsideEndpointDto[] endpoints = BuildOutsideEndpoints(runtime.Entity, waypoints);

                lines.Add(new DispatchPlannerLineDto
                {
                    id = runtime.Id,
                    entityIndex = runtime.Entity.Index,
                    name = runtime.Name ?? string.Empty,
                    kind = runtime.Kind ?? "local",
                    configuredKind = GetKind(runtime.Entity),
                    transportType = runtime.TransportType ?? string.Empty,
                    routeNumber = runtime.RouteNumber == int.MaxValue ? -1 : runtime.RouteNumber,
                    stationCount = lineStations.Count,
                    color = runtime.Color ?? string.Empty,
                    originStationId = runtime.OriginStationId ?? string.Empty,
                    originStationName = runtime.OriginStationName ?? string.Empty,
                    originHoldLimitMinutes = GetHold(runtime.Entity),
                    maxStationDwellMinutes = GetDwell(runtime.Entity),
                    allowedDepotId = GetDepotId(runtime.Id),
                    hasTimeProfile = hasProfile,
                    estimatedLoopMinutes = hasProfile ? RoundPlannerMinutes(profile.m_BaseLoopFrames) : 0f,
                    outsideEndpoints = endpoints
                });
            }

            List<DispatchPlannerTraversalSliceDto> traversalSlices;
            DispatchPlannerTrackScenarioDto currentTrackScenario = BuildPlannerTrackScenario(
                scope,
                runtimeLines,
                stationRecordsByLine,
                out traversalSlices);

            DispatchPlannerObservationSummaryDto observations = BuildPlannerObservationSummary(
                scope,
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
                mode = scope.Token,
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
                drafts = BuildPlannerDrafts(scope, runtimeLines)
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
                Entity stopEntity = Stop(waypoint);
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

                Entity building = StationOf(stopEntity);
                if (building == Entity.Null)
                {
                    building = ResolvePassingStationBuilding(stopEntity);
                }

                string name = StationName(stopEntity);
                if (string.IsNullOrEmpty(name))
                {
                    name = "Stop " + (stations.Count + 1).ToString();
                }

                string workbenchStationId = StationId(stations.Count);
                string stationId = CreatePlannerStationId(runtime.Id, stations.Count);
                float profileDwellMinutes = hasProfile ? RoundPlannerMinutes(ProfileStopFrames(profile, i)) : 0f;
                bool hasObservedDwell = TryGetPlannerObservedStopDwell(
                    runtime.Entity,
                    i,
                    out float observedDwellFrames,
                    out int observedDwellSampleCount,
                    out string observedDwellSource);

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
                    canConfigureBypass = building != Entity.Null && ResolvePassingStationBuilding(building) != Entity.Null,
                    isConfiguredBypass = building != Entity.Null && IsBypassStationSetting(building),
                    profileDwellMinutes = profileDwellMinutes,
                    observedDwellMinutes = hasObservedDwell ? RoundPlannerMinutes(observedDwellFrames) : 0f,
                    observedDwellSampleCount = hasObservedDwell ? observedDwellSampleCount : 0,
                    dwellSource = hasObservedDwell ? observedDwellSource : hasProfile ? "profile" : "unavailable",
                    confidence = hasObservedDwell ? ComputePlannerSampleConfidence(observedDwellSampleCount) : hasProfile ? 0.55f : 0.2f
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

        private bool TryGetPlannerObservedStopDwell(
            Entity line,
            int waypointIndex,
            out float averageFrames,
            out int sampleCount,
            out string source)
        {
            averageFrames = 0f;
            sampleCount = 0;
            source = string.Empty;

            if (TryResolveStationStopDwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
            {
                string observationKey = MakeStationDwellObservationKey(line, anchor.StationAnchorId);
                if (!string.IsNullOrWhiteSpace(observationKey)
                    && R.m_Observation.TryStationDwell(observationKey, out StationDwellObservation anchorObservation)
                    && anchorObservation.SampleCount > 0
                    && anchorObservation.AverageFrames > 0f)
                {
                    averageFrames = anchorObservation.AverageFrames;
                    sampleCount = anchorObservation.SampleCount;
                    source = "anchorObserved";
                    return true;
                }
            }

            return false;
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
            ModeScope scope,
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
                DispatchPlannerLineTrackDto lineTrack = BuildPlannerLineTrack(scope, runtime, waypoints, stationRecords);
                lineTracks.Add(lineTrack);
                if (lineTrack.available
                    && m_TrackModel.TryChain(runtime.Entity, out LineTrackChain chain)
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
            foreach (LineTrackChain chain in chainByLineId.Values)
            {
                m_TrackModel.ResetBypassPipeline(chain);
            }

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
            ModeScope scope,
            WorkbenchLineRuntime runtime,
            DynamicBuffer<RouteWaypoint> waypoints,
            List<PlannerStationRecord> stationRecords)
        {
            if (!m_TrackModel.TryGetChainForLine(runtime.Entity, waypoints, out LineTrackChain chain)
                || chain == null)
            {
                return new DispatchPlannerLineTrackDto
                {
                    lineId = runtime.Id,
                    available = false,
                    unavailableReason = "no-track-chain",
                    protectedIntervals = Array.Empty<DispatchPlannerProtectedIntervalDto>(),
                    traversalSlices = Array.Empty<DispatchPlannerTraversalSliceDto>(),
                    trackAtoms = Array.Empty<DispatchPlannerTrackAtomDto>()
                };
            }

            m_TrackModel.EnsureBypassPipelineReady(chain, scope);
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
                executionMode = "static-track-model",
                protectedIntervals = BuildPlannerProtectedIntervals(runtime.Id, chain, stationRecords),
                traversalSlices = BuildPlannerTraversalSlices(runtime.Id, runtime.Entity, chain),
                trackAtoms = BuildPlannerTrackAtoms(chain)
            };
        }

        private DispatchPlannerTrackAtomDto[] BuildPlannerTrackAtoms(LineTrackChain chain)
        {
            if (chain == null || chain.TrackAtoms == null || chain.TrackAtoms.Count == 0)
                return Array.Empty<DispatchPlannerTrackAtomDto>();

            List<DispatchPlannerTrackAtomDto> atoms = new List<DispatchPlannerTrackAtomDto>(chain.TrackAtoms.Count);
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                bool hasCurve = TryGetPlannerTrackAtomCurve(atom, out Entity curveEntity, out Curve curve);
                bool hasTrackLane = TryGetPlannerTrackAtomTrackLane(atom, out TrackLane trackLane);
                float traversalLengthMeters = 0f;
                if (hasCurve)
                {
                    float start = math.saturate(atom.TargetDelta.x);
                    float end = math.saturate(atom.TargetDelta.y);
                    if (math.abs(end - start) > 0.0001f)
                    {
                        Bounds1 bounds = new Bounds1(math.min(start, end), math.max(start, end));
                        traversalLengthMeters = MathUtils.Length(curve.m_Bezier.xz, bounds);
                    }
                }

                atoms.Add(new DispatchPlannerTrackAtomDto
                {
                    atomIndex = atomIndex,
                    sourceTargetEntityIndex = atom.SourceTarget == Entity.Null ? -1 : atom.SourceTarget.Index,
                    physicalLaneEntityIndex = atom.Key.PhysicalLaneKey == Entity.Null ? -1 : atom.Key.PhysicalLaneKey.Index,
                    targetDeltaStart = atom.TargetDelta.x,
                    targetDeltaEnd = atom.TargetDelta.y,
                    sourceFlags = atom.SourceFlags.ToString(),
                    atomClass = atom.AtomClass.ToString(),
                    traversalDir = atom.TraversalDir.ToString(),
                    hasCurve = hasCurve,
                    curveEntityIndex = hasCurve ? curveEntity.Index : -1,
                    curveLengthMeters = hasCurve ? curve.m_Length : 0f,
                    traversalLengthMeters = traversalLengthMeters,
                    bezierAx = hasCurve ? curve.m_Bezier.a.x : 0f,
                    bezierAy = hasCurve ? curve.m_Bezier.a.y : 0f,
                    bezierAz = hasCurve ? curve.m_Bezier.a.z : 0f,
                    bezierBx = hasCurve ? curve.m_Bezier.b.x : 0f,
                    bezierBy = hasCurve ? curve.m_Bezier.b.y : 0f,
                    bezierBz = hasCurve ? curve.m_Bezier.b.z : 0f,
                    bezierCx = hasCurve ? curve.m_Bezier.c.x : 0f,
                    bezierCy = hasCurve ? curve.m_Bezier.c.y : 0f,
                    bezierCz = hasCurve ? curve.m_Bezier.c.z : 0f,
                    bezierDx = hasCurve ? curve.m_Bezier.d.x : 0f,
                    bezierDy = hasCurve ? curve.m_Bezier.d.y : 0f,
                    bezierDz = hasCurve ? curve.m_Bezier.d.z : 0f,
                    hasTrackLane = hasTrackLane,
                    speedLimitMetersPerSecond = hasTrackLane ? trackLane.m_SpeedLimit : 0f,
                    curviness = hasTrackLane ? trackLane.m_Curviness : 0f,
                    trackLaneFlags = hasTrackLane ? trackLane.m_Flags.ToString() : string.Empty,
                    trackLaneFlagsRaw = hasTrackLane ? (int)trackLane.m_Flags : 0
                });
            }

            return atoms.ToArray();
        }

        private bool TryGetPlannerTrackAtomCurve(TrackAtom atom, out Entity curveEntity, out Curve curve)
        {
            curveEntity = Entity.Null;
            curve = default;
            if (TryGetPlannerEntityCurve(atom.SourceTarget, out curve))
            {
                curveEntity = atom.SourceTarget;
                return true;
            }

            if (atom.Key.PhysicalLaneKey != atom.SourceTarget
                && TryGetPlannerEntityCurve(atom.Key.PhysicalLaneKey, out curve))
            {
                curveEntity = atom.Key.PhysicalLaneKey;
                return true;
            }

            return false;
        }

        private bool TryGetPlannerEntityCurve(Entity entity, out Curve curve)
        {
            curve = default;
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Curve>(entity))
            {
                return false;
            }

            curve = EntityManager.GetComponentData<Curve>(entity);
            return true;
        }

        private bool TryGetPlannerTrackAtomTrackLane(TrackAtom atom, out TrackLane trackLane)
        {
            trackLane = default;
            if (TryGetPlannerEntityTrackLane(atom.Key.PhysicalLaneKey, out trackLane))
                return true;

            return atom.SourceTarget != atom.Key.PhysicalLaneKey
                && TryGetPlannerEntityTrackLane(atom.SourceTarget, out trackLane);
        }

        private bool TryGetPlannerEntityTrackLane(Entity entity, out TrackLane trackLane)
        {
            trackLane = default;
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<TrackLane>(entity))
            {
                return false;
            }

            trackLane = EntityManager.GetComponentData<TrackLane>(entity);
            return true;
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
                bool hasObservation = R.m_Observation.TrySlice(
                    Keys.Slice(line, slice.SliceIndex),
                    out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;
                bool hasStartEvent = TryGetPlannerTraversalEvent(chain, slice.StartEventIndex, out TraversalEvent startEvent);
                bool hasEndEvent = TryGetPlannerTraversalEvent(chain, slice.EndEventIndex, out TraversalEvent endEvent);
                string startEventKind = hasStartEvent
                    ? FormatPlannerTraversalEventKind(startEvent.Kind)
                    : "unknown";
                string endEventKind = hasEndEvent
                    ? FormatPlannerTraversalEventKind(endEvent.Kind)
                    : "unknown";
                string stationTraversalKind = "none";
                int stationWaypointIndex = -1;
                float stationStopMinutes = 0f;
                bool observedIncludesStationStop = false;
                if (TryGetPlannerTraversalStationEventForSlice(chain, slice, out TraversalEvent stationEvent))
                {
                    stationTraversalKind = FormatPlannerTraversalStationKind(stationEvent.Kind);
                    stationWaypointIndex = stationEvent.WaypointIndex;
                    stationStopMinutes = RoundPlannerMinutes(stationEvent.StopFrames);
                    observedIncludesStationStop = hasObservation && stationEvent.Kind == TraversalEventKind.Stop;
                }

                slices.Add(new DispatchPlannerTraversalSliceDto
                {
                    id = lineId + ":slice-" + slice.SliceIndex.ToString(),
                    lineId = lineId,
                    sliceIndex = slice.SliceIndex,
                    startAtomIndex = slice.StartAtomIndex,
                    endAtomIndexExclusive = slice.EndAtomIndexExclusive,
                    physicalLaneCount = slice.PhysicalLaneKeys != null ? slice.PhysicalLaneKeys.Length : 0,
                    startEventKind = startEventKind,
                    endEventKind = endEventKind,
                    startWaypointIndex = hasStartEvent ? startEvent.WaypointIndex : -1,
                    endWaypointIndex = hasEndEvent ? endEvent.WaypointIndex : -1,
                    stationTraversalKind = stationTraversalKind,
                    stationWaypointIndex = stationWaypointIndex,
                    stationStopMinutes = stationStopMinutes,
                    observedIncludesStationStop = observedIncludesStationStop,
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

        private static bool TryGetPlannerTraversalEvent(
            LineTrackChain chain,
            int eventIndex,
            out TraversalEvent traversalEvent)
        {
            traversalEvent = default;
            if (chain?.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || eventIndex < 0
                || eventIndex >= chain.TraversalProfile.Events.Count)
            {
                return false;
            }

            traversalEvent = chain.TraversalProfile.Events[eventIndex];
            return true;
        }

        private static bool TryGetPlannerTraversalStationEventForSlice(
            LineTrackChain chain,
            TraversalRunSlice slice,
            out TraversalEvent traversalEvent)
        {
            traversalEvent = default;
            if (chain?.TraversalProfile == null || chain.TraversalProfile.Events == null)
                return false;

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[i];
                if ((candidate.Kind == TraversalEventKind.Stop || candidate.Kind == TraversalEventKind.Pass)
                    && candidate.StartAtomIndex == slice.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == slice.EndAtomIndexExclusive)
                {
                    traversalEvent = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string FormatPlannerTraversalEventKind(TraversalEventKind kind)
        {
            switch (kind)
            {
                case TraversalEventKind.Stop:
                    return "stop";
                case TraversalEventKind.Pass:
                    return "pass";
                case TraversalEventKind.ApproachSplitBoundary:
                    return "approach";
                case TraversalEventKind.DepartureSplitBoundary:
                    return "departure";
                default:
                    return "unknown";
            }
        }

        private static string FormatPlannerTraversalStationKind(TraversalEventKind kind)
        {
            if (kind == TraversalEventKind.Stop)
                return "stop";
            if (kind == TraversalEventKind.Pass)
                return "pass";
            return "none";
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

                    GlobalSharedTrunkSnapshot snapshot = m_Bypass.GetGlobalSharedTrunkSnapshotCurrent(leftEntry.Value, rightEntry.Value);
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
            ModeScope scope,
            List<PlannerStationRecord> stationRecords,
            List<DispatchPlannerTraversalSliceDto> traversalSlices)
        {
            List<DispatchPlannerStationDwellObservationDto> stopDwell = new List<DispatchPlannerStationDwellObservationDto>();
            int stopDwellSampleCount = 0;
            for (int i = 0; i < stationRecords.Count; i++)
            {
                PlannerStationRecord station = stationRecords[i];
                if (!TryGetPlannerObservedStopDwell(
                        station.LineEntity,
                        station.WaypointIndex,
                        out float averageFrames,
                        out int sampleCount,
                        out string source))
                {
                    continue;
                }

                stopDwellSampleCount += sampleCount;
                stopDwell.Add(new DispatchPlannerStationDwellObservationDto
                {
                    stationId = station.Dto.id,
                    lineId = station.Dto.lineId,
                    waypointIndex = station.WaypointIndex,
                    averageMinutes = RoundPlannerMinutes(averageFrames),
                    sampleCount = sampleCount,
                    source = source,
                    confidence = ComputePlannerSampleConfidence(sampleCount)
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
                traversalSlices = traversalSlices.ToArray(),
                traversalSliceActualSamples = BuildPlannerTraversalSliceActualSamples(scope),
                traversalPositionSamples = BuildPlannerTraversalPositionSamples(scope)
            };
        }

        private DispatchPlannerTraversalSliceActualSampleDto[] BuildPlannerTraversalSliceActualSamples(ModeScope scope)
        {
            List<DispatchPlannerTraversalSliceActualSampleDto> samples =
                new List<DispatchPlannerTraversalSliceActualSampleDto>();
            foreach (TraversalSliceActualSample sample in R.m_Observation.ActualSamples)
            {
                string sampleLineId = ResolvePlannerSampleLineId(scope, sample.Line);
                if (string.IsNullOrEmpty(sampleLineId))
                    continue;

                float durationFrames = sample.ExitFrame > sample.EnterFrame
                    ? sample.ExitFrame - sample.EnterFrame
                    : 0f;
                samples.Add(new DispatchPlannerTraversalSliceActualSampleDto
                {
                    lineId = sampleLineId,
                    lineEntityIndex = sample.Line == Entity.Null ? -1 : sample.Line.Index,
                    vehicleEntityIndex = sample.Vehicle == Entity.Null ? -1 : sample.Vehicle.Index,
                    sliceIndex = sample.SliceIndex,
                    enterFrame = sample.EnterFrame,
                    exitFrame = sample.ExitFrame,
                    durationMinutes = (float)R.m_SimClock.ToMinutes(durationFrames),
                    enterAtomIndex = sample.EnterAtomIndex,
                    enterAtomPosition01 = sample.EnterAtomPosition01,
                    exitAtomIndex = sample.ExitAtomIndex,
                    exitAtomPosition01 = sample.ExitAtomPosition01
                });
            }

            return samples.ToArray();
        }

        private DispatchPlannerTraversalPositionSampleDto[] BuildPlannerTraversalPositionSamples(ModeScope scope)
        {
            List<DispatchPlannerTraversalPositionSampleDto> samples =
                new List<DispatchPlannerTraversalPositionSampleDto>();
            foreach (TraversalPositionSample sample in R.m_Observation.PositionSamples)
            {
                string sampleLineId = ResolvePlannerSampleLineId(scope, sample.Line);
                if (string.IsNullOrEmpty(sampleLineId))
                    continue;

                samples.Add(new DispatchPlannerTraversalPositionSampleDto
                {
                    lineId = sampleLineId,
                    lineEntityIndex = sample.Line == Entity.Null ? -1 : sample.Line.Index,
                    vehicleEntityIndex = sample.Vehicle == Entity.Null ? -1 : sample.Vehicle.Index,
                    frame = sample.Frame,
                    sliceIndex = sample.SliceIndex,
                    segmentIndex = sample.SegmentIndex,
                    segmentPosition = sample.SegmentPosition,
                    atomIndex = sample.AtomIndex,
                    atomPosition01 = sample.AtomPosition01,
                    physicalLaneEntityIndex = sample.PhysicalLane == Entity.Null ? -1 : sample.PhysicalLane.Index,
                    speedMetersPerSecond = sample.SpeedMetersPerSecond,
                    odometerMeters = sample.OdometerMeters
                });
            }

            return samples.ToArray();
        }

        private string ResolvePlannerSampleLineId(ModeScope scope, Entity line)
        {
            if (line == Entity.Null)
                return string.Empty;

            string lineId = LineId(line);
            if (LineKey.TryParse(lineId, out LineKey key))
                return key.Mode == scope.Mode ? lineId : string.Empty;

            TransitMode resolvedMode = TransportModeResolver.Resolve(EntityManager, line);
            if (resolvedMode != scope.Mode)
                return string.Empty;

            return string.IsNullOrWhiteSpace(lineId)
                ? string.Empty
                : scope.NormalizeLineId(lineId);
        }

        private static bool MatchesScope(WorkbenchLineRuntime line, ModeScope scope)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.Id))
                return false;

            if (LineKey.TryParse(line.Id, out LineKey key))
                return key.Mode == scope.Mode;

            TransitMode resolvedMode = TransportModeResolver.Resolve(line.TransportType);
            return resolvedMode == scope.Mode;
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
                if (!configuredOnly && station.Dto.isConfiguredBypass)
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
                simFramesPerMinute = R.m_SimClock.Snapshot.FramesPerMinute,
                clockEpoch = R.m_SimClock.Snapshot.ClockEpoch,
                defaultOriginHoldLimitMinutes = RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes,
                defaultMaxStationDwellMinutes = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes,
                trackModelEntryClearSafetyGapMinutes = AdmissionService.TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES,
                localBypassExitReleaseAtoms = LOCAL_BYPASS_EXIT_RELEASE_ATOMS,
                localBypassTrainTailClearAtoms = AdmissionService.LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS,
                minStrongProtectedIntervalOverlapAtoms = AdmissionService.MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS,
                minStrongProtectedIntervalOrderedRun = AdmissionService.MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN,
                compatibilityMode = "read-only-planner-input"
            };
        }

        private DispatchPlannerDraftDto[] BuildPlannerDrafts(ModeScope scope, List<WorkbenchLineRuntime> runtimeLines)
        {
            List<DispatchPlannerDraftDto> drafts = new List<DispatchPlannerDraftDto>();
            HashSet<string> runtimeLineIds = new HashSet<string>(
                (runtimeLines ?? new List<WorkbenchLineRuntime>())
                    .Where(line => !string.IsNullOrEmpty(line?.Id))
                    .Select(line => line.Id),
                StringComparer.Ordinal);
            Dictionary<string, string> sourceKeyByDraftKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string sourceKey in m_WorkbenchBridge.DraftStore.Keys)
            {
                if (TryNormalizePlannerExportDraftKey(scope, sourceKey, runtimeLineIds, out string normalizedDraftKey)
                    && !sourceKeyByDraftKey.ContainsKey(normalizedDraftKey))
                {
                    sourceKeyByDraftKey[normalizedDraftKey] = sourceKey;
                }
            }
            for (int i = 0; i < (runtimeLines?.Count ?? 0); i++)
            {
                string runtimeLineId = runtimeLines[i]?.Id;
                if (!string.IsNullOrEmpty(runtimeLineId) && !sourceKeyByDraftKey.ContainsKey(runtimeLineId))
                {
                    sourceKeyByDraftKey[runtimeLineId] = string.Empty;
                }
            }

            foreach (string draftKey in sourceKeyByDraftKey.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                string sourceKey = sourceKeyByDraftKey[draftKey];
                DispatchWorkbenchDraftState sourceDraft = null;
                if (!string.IsNullOrEmpty(sourceKey))
                {
                    m_WorkbenchBridge.DraftStore.TryGetValue(sourceKey, out sourceDraft);
                }
                DispatchWorkbenchDraftState draft = ClonePlannerExportDraftState(scope, draftKey, sourceDraft, runtimeLines, runtimeLineIds);
                string preferredLineId = !string.IsNullOrEmpty(draft?.SelectedLineId)
                    ? draft.SelectedLineId
                    : !string.IsNullOrEmpty(draft?.MergedView?.localLineId)
                        ? draft.MergedView.localLineId
                        : draftKey;
                WorkbenchLineRuntime activeRuntime = runtimeLines != null && runtimeLines.Count > 0
                    ? ActiveLine(runtimeLines, preferredLineId)
                    : null;
                List<DispatchWorkbenchStationDto> workbenchStations = activeRuntime != null
                    ? Stations(activeRuntime.Entity)
                    : new List<DispatchWorkbenchStationDto>();
                List<DispatchWorkbenchTripDto> trips = activeRuntime != null
                    ? Trips().Build(activeRuntime, workbenchStations, draft)
                    : new List<DispatchWorkbenchTripDto>();
                drafts.Add(new DispatchPlannerDraftDto
                {
                    lineKey = draftKey,
                    selectedLineId = draft.SelectedLineId ?? string.Empty,
                    selectedEditLine = draft.SelectedEditLine ?? string.Empty,
                    mergedView = draft.MergedView,
                    stagedRows = draft.StagedRows != null ? draft.StagedRows.ToArray() : Array.Empty<DispatchWorkbenchStagedRowDto>(),
                    trips = trips.ToArray()
                });
            }

            return drafts.ToArray();
        }

        private DispatchWorkbenchDraftState ClonePlannerExportDraftState(
            ModeScope scope,
            string draftKey,
            DispatchWorkbenchDraftState sourceDraft,
            List<WorkbenchLineRuntime> runtimeLines,
            HashSet<string> runtimeLineIds)
        {
            DispatchWorkbenchDraftState draft = sourceDraft != null
                ? new DispatchWorkbenchDraftState
                {
                    SelectedLineId = sourceDraft.SelectedLineId ?? string.Empty,
                    SelectedEditLine = sourceDraft.SelectedEditLine ?? string.Empty,
                    MergedView = sourceDraft.MergedView == null
                        ? null
                        : new DispatchWorkbenchMergedView
                        {
                            localLineId = sourceDraft.MergedView.localLineId,
                            expressLineId = sourceDraft.MergedView.expressLineId,
                            localLineIds = sourceDraft.MergedView.localLineIds != null ? sourceDraft.MergedView.localLineIds.ToArray() : Array.Empty<string>(),
                            expressLineIds = sourceDraft.MergedView.expressLineIds != null ? sourceDraft.MergedView.expressLineIds.ToArray() : Array.Empty<string>(),
                            isLoop = sourceDraft.MergedView.isLoop,
                            turnbackStationId = sourceDraft.MergedView.turnbackStationId,
                            direction = sourceDraft.MergedView.direction,
                            windowStart = sourceDraft.MergedView.windowStart,
                            windowEnd = sourceDraft.MergedView.windowEnd
                        },
                    StagedRows = sourceDraft.StagedRows != null ? sourceDraft.StagedRows.Select(CopyRow).ToList() : new List<DispatchWorkbenchStagedRowDto>(),
                    DraftApplied = sourceDraft.DraftApplied
                }
                : DraftStore().New(draftKey);

            NormalizePlannerExportDraft(scope, draftKey, draft, runtimeLineIds);
            WorkbenchLineRuntime activeRuntime = runtimeLines != null && runtimeLines.Count > 0
                ? ActiveLine(runtimeLines, draft.SelectedLineId ?? draftKey)
                : null;
            if (sourceDraft == null && activeRuntime != null)
            {
                if (string.Equals(activeRuntime.Kind, "express", StringComparison.OrdinalIgnoreCase))
                {
                    draft.MergedView.localLineIds = Array.Empty<string>();
                    draft.MergedView.localLineId = string.Empty;
                    draft.MergedView.expressLineIds = new[] { activeRuntime.Id };
                    draft.MergedView.expressLineId = activeRuntime.Id;
                }
                else
                {
                    draft.MergedView.localLineIds = new[] { activeRuntime.Id };
                    draft.MergedView.localLineId = activeRuntime.Id;
                    draft.MergedView.expressLineIds = Array.Empty<string>();
                    draft.MergedView.expressLineId = string.Empty;
                }
            }

            return draft;
        }

        private static bool TryNormalizePlannerExportDraftKey(
            ModeScope scope,
            string sourceKey,
            HashSet<string> runtimeLineIds,
            out string normalizedDraftKey)
        {
            normalizedDraftKey = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceKey)
                || string.Equals(sourceKey, "__default__", StringComparison.Ordinal))
            {
                return false;
            }

            if (LineKey.TryParse(sourceKey, out LineKey key))
            {
                if (key.Mode != scope.Mode)
                    return false;
                normalizedDraftKey = sourceKey;
            }
            else
            {
                if (scope.Mode != ModeScope.DefaultWorkbench.Mode)
                    return false;
                normalizedDraftKey = scope.NormalizeLineId(sourceKey);
            }

            return !string.IsNullOrEmpty(normalizedDraftKey)
                && (runtimeLineIds == null || runtimeLineIds.Contains(normalizedDraftKey));
        }

        private static void NormalizePlannerExportDraft(
            ModeScope scope,
            string draftKey,
            DispatchWorkbenchDraftState draft,
            HashSet<string> runtimeLineIds)
        {
            if (draft == null)
                return;

            draft.SelectedLineId = NormalizePlannerExportLineId(scope, draft.SelectedLineId, runtimeLineIds);
            if (string.IsNullOrEmpty(draft.SelectedLineId))
                draft.SelectedLineId = draftKey ?? string.Empty;

            draft.SelectedEditLine = NormalizePlannerExportLineId(scope, draft.SelectedEditLine, runtimeLineIds);
            if (string.IsNullOrEmpty(draft.SelectedEditLine))
                draft.SelectedEditLine = draft.SelectedLineId;

            if (draft.MergedView != null)
            {
                draft.MergedView.localLineId = NormalizePlannerExportLineId(scope, draft.MergedView.localLineId, runtimeLineIds);
                draft.MergedView.expressLineId = NormalizePlannerExportLineId(scope, draft.MergedView.expressLineId, runtimeLineIds);
                draft.MergedView.localLineIds = NormalizePlannerExportLineIds(scope, draft.MergedView.localLineIds, runtimeLineIds);
                draft.MergedView.expressLineIds = NormalizePlannerExportLineIds(scope, draft.MergedView.expressLineIds, runtimeLineIds);
            }

            draft.StagedRows = (draft.StagedRows ?? new List<DispatchWorkbenchStagedRowDto>())
                .Select(row => NormalizePlannerExportStagedRow(scope, row, runtimeLineIds))
                .Where(row => row != null)
                .ToList();
        }

        private static string[] NormalizePlannerExportLineIds(
            ModeScope scope,
            string[] lineIds,
            HashSet<string> runtimeLineIds)
        {
            return (lineIds ?? Array.Empty<string>())
                .Select(lineId => NormalizePlannerExportLineId(scope, lineId, runtimeLineIds))
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string NormalizePlannerExportLineId(
            ModeScope scope,
            string lineId,
            HashSet<string> runtimeLineIds)
        {
            if (string.IsNullOrWhiteSpace(lineId) || !scope.MatchesLineId(lineId))
                return string.Empty;

            string normalized = scope.NormalizeLineId(lineId);
            return runtimeLineIds == null || runtimeLineIds.Contains(normalized)
                ? normalized
                : string.Empty;
        }

        private static DispatchWorkbenchStagedRowDto NormalizePlannerExportStagedRow(
            ModeScope scope,
            DispatchWorkbenchStagedRowDto row,
            HashSet<string> runtimeLineIds)
        {
            if (row == null)
                return null;

            row.lineId = NormalizePlannerExportLineId(scope, row.lineId, runtimeLineIds);
            return string.IsNullOrEmpty(row.lineId) ? null : row;
        }

        private bool TryResolvePlannerWaypointPosition(Entity waypoint, out float3 position) => R.m_MileageStore.TryWaypointPosition(waypoint, out position);

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

            return R.m_LineTimes.Segment(profile, segmentIndex);
        }

        private float ProfileStopFrames(LineTimeProfileHeader profile, int stopIndex)
        {
            if (stopIndex < 0 || stopIndex >= profile.m_Count)
                return 0f;

            return R.m_LineTimes.StopValue(profile, stopIndex);
        }

        private static string CreatePlannerStationId(string lineId, int order)
        {
            return (lineId ?? string.Empty) + ":station-" + order.ToString();
        }

        private float RoundPlannerMinutes(float frames)
        {
            if (!(frames > 0f))
                return 0f;

            return (float)Math.Round(R.m_SimClock.ToMinutes(frames), 2);
        }

        private DispatchPlannerOutsideEndpointDto[] BuildOutsideEndpoints(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            List<DispatchPlannerOutsideEndpointDto> endpoints = new List<DispatchPlannerOutsideEndpointDto>();
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (RouteWaypointEndpointResolver.TryResolveRouteWaypointEndpoint(EntityManager, waypoint, out RouteWaypointEndpoint endpoint))
                {
                    string direction = endpoint.Direction == RouteWaypointEndpointDirection.Entry ? "entry"
                        : endpoint.Direction == RouteWaypointEndpointDirection.Exit ? "exit"
                        : endpoint.Direction == RouteWaypointEndpointDirection.Boundary ? "boundary"
                        : "unknown";
                    string kind = endpoint.Kind == RouteWaypointEndpointKind.OutsideTrainConnection ? "outside-train" : "unknown";

                    endpoints.Add(new DispatchPlannerOutsideEndpointDto
                    {
                        waypointIndex = i,
                        direction = direction,
                        kind = kind,
                        startLaneIndex = endpoint.StartLane.Index,
                        endLaneIndex = endpoint.EndLane.Index,
                        startCurvePos = endpoint.StartCurvePos,
                        endCurvePos = endpoint.EndCurvePos
                    });
                }
            }
            return endpoints.ToArray();
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
            if (segment.OrderedRun >= AdmissionService.MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
                confidence += 0.05f;
            if (segment.PhysicalOverlap >= AdmissionService.MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS)
                confidence += 0.05f;

            return math.clamp(confidence, 0.2f, 0.9f);
        }
    }
}
