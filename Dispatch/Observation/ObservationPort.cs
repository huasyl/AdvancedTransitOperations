using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class ObservationPort
    {
        private const float RailDispatchSampleOutlierFactor = 1.5f;
        private const float RoadDispatchSampleOutlierFactor = 3.0f;
        private const uint DISPATCH_SAMPLE_MIN_FRAMES = 365u;

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Capture m_Capture;
        private readonly SliceAdmission m_Admission;
        private readonly BusSegCapture m_BusSeg;
        private readonly MonitorAverageStore m_Averages;
        private readonly Func<Entity, Entity> m_Anchor;
        private readonly Func<Entity, string> m_StopKey;
        private readonly Dictionary<Entity, DispatchEtaRequest> m_DispatchEtaRequests =
            new Dictionary<Entity, DispatchEtaRequest>();
        private readonly Dictionary<Entity, uint> m_DispatchTimingCutoffs =
            new Dictionary<Entity, uint>();

        private sealed class DispatchEtaRequest
        {
            public uint DispatchFrame;
        }

        public ObservationPort(
            ModRuntimeHostSystem runtime,
            Capture capture,
            SliceAdmission admission,
            BusSegCapture busSeg,
            MonitorAverageStore averages,
            Func<Entity, Entity> anchor,
            Func<Entity, string> stopKey)
        {
            m_Runtime = runtime;
            m_Capture = capture;
            m_Admission = admission;
            m_BusSeg = busSeg;
            m_Averages = averages ?? throw new ArgumentNullException(nameof(averages));
            m_Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            m_StopKey = stopKey ?? throw new ArgumentNullException(nameof(stopKey));
        }

        public void Record(Entity vehicle, string reason)
        {
            m_Capture.RecordLapStart(vehicle, reason);
        }

        public void Update(Entity vehicle)
        {
            m_Capture.UpdateLapStats(vehicle);
        }

        public void Finish(Entity vehicle, uint nowFrame, int exitAtomIndex, float exitAtomPosition01)
        {
            m_Capture.FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame, exitAtomIndex, exitAtomPosition01);
            m_Admission.End(vehicle);
        }

        public bool DropSlice(Entity vehicle, out int sliceIndex)
        {
            bool dropped = m_Runtime.m_ObsPersist.DropSlice(vehicle, out sliceIndex);
            m_Admission.End(vehicle);
            return dropped;
        }

        public void ClearVehicleSlices(Entity vehicle)
        {
            m_Runtime.m_ObsPersist.ClearVehicleSlices(vehicle);
            m_Admission.End(vehicle);
        }

        public void InvalidateSliceLine(Entity line)
        {
            m_Runtime.m_Slices.RemoveLine(line);
            m_Admission.InvalidateLine(line);
            m_Runtime.m_ObsBuffers.RemoveSliceLine(line);
        }

        public bool LapTiming(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out float runFrames,
            out float stopFrames,
            out int stopCount,
            out int passCount)
        {
            return m_Capture.TryGetTraversalProfileLapTiming(line, waypoints, out runFrames, out stopFrames, out stopCount, out passCount);
        }

        public void UpdateSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            m_Capture.UpdateVehicleTraversalSliceObservation(vehicle, line, waypoints, nowFrame);
        }

        public bool IsSourceSliceDue(Entity vehicle, Entity line, uint nowFrame)
        {
            return m_Capture.IsSourceSliceDue(vehicle, line, nowFrame);
        }

        public bool BuildPlan(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out TraversalSliceSamplingPlan plan)
        {
            return m_Capture.TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, out plan);
        }

        public bool BuildPlanRaw(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out TraversalSliceSamplingPlan plan)
        {
            return m_Capture.TryBuildTraversalSliceSamplingPlanUncached(vehicle, waypoints, chain, out plan);
        }

        public void RecordSample(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int sliceIndex,
            VehicleTrackCursor cursor,
            uint nowFrame)
        {
            m_Capture.MaybeRecordTraversalPositionSample(vehicle, line, chain, sliceIndex, cursor, nowFrame);
        }

        public bool CurrentSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            return m_Capture.TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, out chain, out sliceIndex, out cursor);
        }

        public bool EffectiveFrames(Entity line, TraversalRunSlice slice, out float frames)
        {
            return m_Capture.TryGetEffectiveTraversalRunSliceFrames(line, slice, out frames);
        }

        public void DebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
        {
            m_Capture.RecordTraversalSliceLapDebugStart(vehicle, slice, atomIndex, atomPosition01);
        }

        public void DebugDrop(Entity vehicle, int sliceIndex)
        {
            m_Capture.RecordTraversalSliceLapDebugDropped(vehicle, sliceIndex);
        }

        public void DebugFinish(Entity vehicle, int sliceIndex, float observedFrames)
        {
            m_Capture.RecordTraversalSliceLapDebugFinalize(vehicle, sliceIndex, observedFrames);
        }

        public void ClearDebug(Entity vehicle)
        {
            m_Capture.ClearVehicleTraversalSliceLapDebug(vehicle);
        }

        public bool DwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            return m_Capture.TryStationDwellAnchor(line, waypointIndex, out anchor);
        }

        public bool TryStationDwell(string key, out StationDwellObservation observation)
        {
            return m_Runtime.m_ObsQuery.TryStationDwell(key, out observation);
        }

        public bool TrySlice(ulong key, out TraversalSliceObservation observation)
        {
            return m_Runtime.m_ObsQuery.TrySlice(key, out observation);
        }

        public void BeginBusSeg(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            m_BusSeg.Begin(vehicle, line, waypointIndex, nowFrame);
        }

        public bool TryEndBusSeg(
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint nowFrame,
            out BusSegSample sample)
        {
            return m_BusSeg.TryEnd(vehicle, line, waypointIndex, nowFrame, out sample);
        }

        public void CancelBusSeg(Entity vehicle)
        {
            m_BusSeg.Cancel(vehicle);
        }

        public void RemoveBusSegVehicle(Entity vehicle)
        {
            m_BusSeg.RemoveVehicle(vehicle);
        }

        public bool TryBusSegFrames(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop,
            out float frames)
        {
            frames = 0f;
            return m_Runtime.m_ObsQuery.TryBusSeg(
                new BusSegKey(line, fromWaypoint, fromStop, toWaypoint, toStop),
                out BusSegObservation observation)
                && (frames = observation.EstimatedFrames) > 0f;
        }

        public bool TryTraversalFrames(
            Entity line,
            int fromWaypointIndex,
            int toWaypointIndex,
            out float frames)
        {
            return m_Capture.TryGetObservedTraversalFrames(
                line,
                fromWaypointIndex,
                toWaypointIndex,
                out frames);
        }

        public bool TryTraversalFrames(
            Entity line,
            int fromWaypointIndex,
            int toWaypointIndex,
            out float frames,
            out string detail)
        {
            return m_Capture.TryGetObservedTraversalFrames(
                line,
                fromWaypointIndex,
                toWaypointIndex,
                out frames,
                out detail);
        }

        public void InvalidateBusRoute(
            Entity line,
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            m_BusSeg.InvalidateRoute(line, oldRoute, newRoute);
        }

        public void ClearBusSeg()
        {
            m_BusSeg.Clear();
        }

        public bool TryLapFrames(Entity vehicle, out uint lapFrames)
        {
            return m_Runtime.m_ObsQuery.TryLapFrames(vehicle, out lapFrames);
        }

        public bool TryLapStartFrame(Entity vehicle, out uint lapStartFrame)
        {
            return m_Runtime.m_ObsQuery.TryLapStartFrame(vehicle, out lapStartFrame);
        }

        public IReadOnlyList<TraversalSliceActualSample> ActualSamples => m_Runtime.m_ObsQuery.ActualSamples;

        public IReadOnlyList<TraversalPositionSample> PositionSamples => m_Runtime.m_ObsQuery.PositionSamples;

        public string DwellKey(Entity line, string stationAnchorId)
        {
            return m_Capture.StationDwellKey(line, stationAnchorId);
        }

        public void ClearDwellDeadlineCache(Entity vehicle)
        {
            m_Runtime.m_StopRuntime.ClearDwellDeadline(vehicle);
        }

        public void ClearDwellDeadlineCache()
        {
            m_Runtime.m_StopRuntime.ClearDwellDeadlines();
        }

        public bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
        {
            dwellFrames = 0f;
            if (line == Entity.Null || waypointIndex < 0)
                return false;

            if (!DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
                return false;

            return m_Capture.TryGetObservedWaypointStopFrames(line, waypointIndex, anchor.StationAnchorId, out dwellFrames);
        }

        public bool TryObservedWaypointDwell(
            Entity line,
            int waypointIndex,
            out StationDwellObservation observation)
        {
            observation = default;
            if (line == Entity.Null
                || waypointIndex < 0
                || !DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
            {
                return false;
            }

            return m_Runtime.m_ObsQuery.TryStationDwell(
                DwellKey(line, anchor.StationAnchorId),
                out observation)
                && observation.SampleCount > 0
                && observation.AverageFrames > 0f
                && !float.IsNaN(observation.AverageFrames)
                && !float.IsInfinity(observation.AverageFrames);
        }

        public bool Head(Entity vehicle, int waypointIndex, out TrainHeadSnapshot snapshot)
        {
            snapshot = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return false;

            Entity headVehicle = vehicle;
            if (m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length > 0 && layout[0].m_Vehicle != Entity.Null)
                    headVehicle = layout[0].m_Vehicle;
            }

            if (headVehicle == Entity.Null
                || !m_Runtime.EntityManager.Exists(headVehicle)
                || !m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(headVehicle))
            {
                return false;
            }

            TrainCurrentLane currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(headVehicle);
            bool reversed = m_Runtime.EntityManager.HasComponent<Train>(headVehicle)
                && (m_Runtime.EntityManager.GetComponentData<Train>(headVehicle).m_Flags & TrainFlags.Reversed) != 0;

            snapshot = new TrainHeadSnapshot(
                m_Runtime.m_SimulationSystem.frameIndex,
                headVehicle,
                currentLane.m_Front.m_Lane,
                currentLane.m_Rear.m_Lane,
                reversed,
                waypointIndex);
            return true;
        }

        public void BeginObservedDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            m_Capture.BeginObservedDwellSession(vehicle, line, waypointIndex, nowFrame);
        }

        public bool TryRecordObservedStopDwellOnBoardingEnd(
            Entity vehicle,
            Entity line,
            int fallbackWaypointIndex,
            uint nowFrame,
            out int observedWaypointIndex)
        {
            observedWaypointIndex = -1;
            if (!m_Capture.TryRecordObservedStopDwellOnBoardingEnd(vehicle, line, fallbackWaypointIndex, nowFrame, out ObservedDwellSample sample))
                return false;

            if (!RecordStationDwellObservation(sample.Line, sample.WaypointIndex, sample.SampleFrames, sample.Frame, sample.SampleMinutes))
                return false;

            observedWaypointIndex = sample.WaypointIndex;
            return true;
        }

        public void Seed(Entity vehicle, Entity line, uint nowFrame)
        {
            if (line == Entity.Null)
            {
                m_Runtime.m_VehicleRegistry.ClearPreparing(vehicle);
                m_Runtime.m_VehicleRegistry.ClearDispatch(vehicle);
                ClearDispatchEta(vehicle);
                return;
            }

            uint sampleFrames = 0;
            uint sampleStart = 0;
            bool hasSample = false;
            if (m_Runtime.m_VehicleView.TryGetDispatch(vehicle, out uint dispatchRequestStart))
            {
                sampleFrames = nowFrame - dispatchRequestStart;
                sampleStart = dispatchRequestStart;
                hasSample = true;
            }
            else if (m_Runtime.m_VehicleView.TryGetPreparing(vehicle, out uint prepStart))
            {
                sampleFrames = nowFrame - prepStart;
                sampleStart = prepStart;
                hasSample = true;
            }

            m_Runtime.m_VehicleRegistry.ClearPreparing(vehicle);
            m_Runtime.m_VehicleRegistry.ClearDispatch(vehicle);
            m_DispatchEtaRequests.Remove(vehicle);
            if (!hasSample || sampleFrames == 0)
                return;
            if (IsDispatchTimingInvalid(line, sampleStart))
                return;

            ClockSnapshot clockSnapshot = m_Runtime.m_SimClock.Snapshot;
            float sampleMinutes = (float)clockSnapshot.ToMinutes(sampleFrames);
            if (sampleFrames < DISPATCH_SAMPLE_MIN_FRAMES)
            {
                if (RtLog.VerboseEnabled)
                {
                    m_Runtime.log.Info("[DispatchSample] line" + line.Index + " vehicle" + vehicle.Index
                        + " sample=" + sampleMinutes.ToString("F1") + "min out-of-range skip");
                }
                return;
            }

            float cachedFrames = m_Runtime.m_DispatchCache.Read(line);
            float outlierFactor = DispatchSampleOutlierFactor(line);
            if (cachedFrames > 0f && sampleFrames > cachedFrames * outlierFactor)
            {
                if (RtLog.VerboseEnabled)
                {
                    float cachedMinutes = (float)clockSnapshot.ToMinutes(cachedFrames);
                    m_Runtime.log.Info("[DispatchSample] line" + line.Index + " vehicle" + vehicle.Index
                        + " sample=" + sampleMinutes.ToString("F1") + "min"
                        + " cached=" + cachedMinutes.ToString("F1") + "min high-outlier skip");
                }
                return;
            }

            int nowMinute = clockSnapshot.NowMinute;
            m_Runtime.m_SelectPanel.RecordLineDispatchSampleSummary(line, nowMinute, vehicle, sampleMinutes);
            m_Runtime.m_DispatchCache.Update(line, vehicle, sampleFrames);
        }

        public void BeginDispatchEta(Entity vehicle, Entity line, uint dispatchFrame)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle != LifecycleKind.Rail
                || m_DispatchEtaRequests.ContainsKey(vehicle))
                return;
            m_DispatchEtaRequests[vehicle] = new DispatchEtaRequest
            {
                DispatchFrame = dispatchFrame
            };
        }

        public void TryRequestDispatchEta(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            if (!m_DispatchEtaRequests.TryGetValue(vehicle, out DispatchEtaRequest request))
                return;
            if (line == Entity.Null
                || IsDispatchTimingInvalid(line, request.DispatchFrame)
                || TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle != LifecycleKind.Rail)
            {
                m_DispatchEtaRequests.Remove(vehicle);
                return;
            }
            if (!m_Runtime.m_VehicleView.TryGetDispatch(vehicle, out _))
                return;
            if (vehicle == Entity.Null || line == Entity.Null || waypoints.Length == 0 || !m_Runtime.EntityManager.Exists(vehicle))
                return;
            if (!m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<PathOwner>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<PathInformation>(vehicle)
                || !m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle)
                || !m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
                return;

            CurrentRoute route = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle);
            Target target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            if (route.m_Route != line)
                return;
            if (target.m_Target != waypoints[0].m_Waypoint)
                return;

            PathOwner pathOwner = m_Runtime.EntityManager.GetComponentData<PathOwner>(vehicle);
            if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Stuck | PathFlags.Obsolete | PathFlags.Updated)) != 0)
                return;
            PathInformation pathInformation = m_Runtime.EntityManager.GetComponentData<PathInformation>(vehicle);
            if (pathInformation.m_Destination != waypoints[0].m_Waypoint)
                return;

            DynamicBuffer<PathElement> path = m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true);
            if (path.Length == 0)
                return;

            DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            if (layout.Length == 0
                || layout[0].m_Vehicle == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<Train>(layout[0].m_Vehicle))
                return;

            // Per-vehicle PathOccupants ETA and its diagnostic log are intentionally paused.
            // Keep only the real spawn-to-path-ready preparation sample used by spawn-lead theory.
            m_Runtime.m_DispatchCache.RecordPrep(line, unchecked(nowFrame - request.DispatchFrame));
            m_DispatchEtaRequests.Remove(vehicle);
        }

        public void ClearDispatchEta(Entity vehicle)
        {
            m_DispatchEtaRequests.Remove(vehicle);
        }

        public void ClearDispatchEta()
        {
            m_DispatchEtaRequests.Clear();
            m_DispatchTimingCutoffs.Clear();
        }

        public void InvalidateDispatchTiming(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_DispatchTimingCutoffs[line] = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_DispatchCache.RemoveDepotTiming(line);
        }

        public void RemoveLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_BusSeg.RemoveLine(line);
            m_Averages.RemoveLine(line);
            m_DispatchTimingCutoffs.Remove(line);
            m_Runtime.m_DispatchCache.RemoveLine(line);
        }

        private bool IsDispatchTimingInvalid(Entity line, uint sampleStart)
        {
            return line != Entity.Null
                && m_DispatchTimingCutoffs.TryGetValue(line, out uint cutoff)
                && sampleStart <= cutoff;
        }

        private float DispatchSampleOutlierFactor(Entity line)
        {
            return TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle == LifecycleKind.Road
                ? RoadDispatchSampleOutlierFactor
                : RailDispatchSampleOutlierFactor;
        }

        public string Json()
        {
            return m_Runtime.m_ObsRecorder?.SnapshotJson() ?? string.Empty;
        }

        public bool MonitorPersistenceHealthy =>
            m_Runtime.m_ObsBuffers.MonitorPersistenceHealthy;

        internal bool MonitorClaimsRestored =>
            m_Runtime.m_ObsRecorder == null || m_Runtime.m_ObsRecorder.MonitorClaimsRestored;

        public void Dump()
        {
            if (!RtLog.DebugToolsEnabled)
                return;

            try
            {
                string json = Json();
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-runtime-observation-latest.json");
                File.WriteAllText(filePath, json);
                Mod.log.Info("[ObservationDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[ObservationDump] export failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Seed(string selectedLineId)
        {
            m_Runtime.m_ObsRecorder?.Seed(selectedLineId);
        }

        public IReadOnlyDictionary<string, LinePlan> Lines()
        {
            Dictionary<string, LinePlan> lines = new Dictionary<string, LinePlan>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, AppliedLine> entry in m_Runtime.AppliedLines)
            {
                AppliedLine applied = entry.Value;
                if (applied == null)
                    continue;

                LinePlan line = new LinePlan
                {
                    Line = applied.LineEntity
                };

                if (applied.StagedRows != null)
                {
                    foreach (DispatchWorkbenchStagedRowDto row in applied.StagedRows)
                    {
                        if (row == null)
                            continue;

                        line.Rows.Add(new RowPlan
                        {
                            Id = row.id ?? string.Empty,
                            LineId = row.lineId ?? string.Empty,
                            Time = row.time ?? string.Empty,
                            Kind = row.kind ?? string.Empty,
                            Source = row.source ?? string.Empty
                        });
                    }
                }

                lines[entry.Key] = line;
            }

            return lines;
        }

        public ContractDto[] Contracts()
        {
            List<ContractDto> contracts = new List<ContractDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchPlannerImportContractDto> entry in m_Runtime.Applied().Refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract = entry.Value;
                if (contract == null)
                    continue;

                DispatchPlannerChangedRowDto[] sourceChangedRows =
                    contract.changedRows
                    ?? Array.Empty<DispatchPlannerChangedRowDto>();
                DispatchPlannerScheduleActionDto[] sourceActions =
                    contract.structuredActions
                    ?? Array.Empty<DispatchPlannerScheduleActionDto>();
                DispatchPlannerRiskItemDto[] sourceRiskItems =
                    contract.riskItems
                    ?? Array.Empty<DispatchPlannerRiskItemDto>();
                DispatchPlannerLineRoleSummaryDto sourceLineRoleSummary =
                    contract.lineRoleSummary;
                string[] sourceSelectedBypassStationIds =
                    contract.selectedBypassStationIds
                    ?? Array.Empty<string>();

                if (sourceChangedRows.Length == 0
                    && sourceActions.Length == 0
                    && sourceRiskItems.Length == 0
                    && sourceLineRoleSummary == null
                    && sourceSelectedBypassStationIds.Length == 0)
                {
                    continue;
                }

                ChangeDto[] changedRows = sourceChangedRows
                    .Select(CopyChange)
                    .ToArray();
                contracts.Add(new ContractDto
                {
                    draftKey = entry.Key,
                    importedFrom = contract.importedFrom ?? string.Empty,
                    importedPlanId = contract.importedPlanId ?? string.Empty,
                    importedObjectiveId = contract.importedObjectiveId ?? string.Empty,
                    importedLineIds = contract.importedLineIds ?? Array.Empty<string>(),
                    requestEcho = CopyEcho(contract.requestEcho),
                    lineRoleSummary = CopyRoleSummary(sourceLineRoleSummary),
                    selectedBypassStationIds = sourceSelectedBypassStationIds,
                    changedRows = changedRows,
                    structuredActions = sourceActions
                        .Select(CopyAction)
                        .ToArray(),
                    riskItems = sourceRiskItems
                        .Select(CopyRisk)
                        .ToArray()
                });
            }

            return contracts.ToArray();
        }

        public void BindTarget(Entity line, Entity vehicle, int targetMinute, uint nowFrame, string reasonCode)
        {
            m_Runtime.m_ObsRecorder?.TargetBound(line, vehicle, targetMinute, nowFrame, reasonCode);
        }

        public MonitorChange Launch(
            Entity line,
            Entity vehicle,
            int targetMinute,
            int actualMinute,
            uint launchFrame,
            bool lateDispatch,
            AppliedMonitorRow row)
        {
            m_Admission.Begin(line, vehicle, targetMinute);
            if (!string.IsNullOrEmpty(row.RowId))
            {
                if (m_Runtime.m_ObsRecorder != null)
                {
                    string tripKey = m_Runtime.m_ObsRecorder.Launch(
                        line,
                        vehicle,
                        row,
                        m_Runtime.m_SimClock.Snapshot,
                        launchFrame,
                        out _);
                    if (!string.IsNullOrEmpty(tripKey)
                        && m_Runtime.m_ObsRecorder.TryMonitor(tripKey, out MonitorTrip trip, out _))
                    {
                        return new MonitorChange(
                            true,
                            trip.Line,
                            trip.ServiceDateKey,
                            trip.Key,
                            0,
                            false);
                    }
                }
            }
            return default;
        }

        public MonitorChange Stop(
            Entity vehicle,
            Entity line,
            Entity waypoint,
            Entity station,
            ResolvedStopKind kind,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            string clockTime,
            uint frame)
        {
            if (m_Runtime.m_ObsRecorder != null
                && m_Runtime.m_ObsRecorder.Stop(
                    vehicle,
                    line,
                    station,
                    m_StopKey(m_Anchor(station)),
                    kind,
                    waypointIndex,
                    isOrigin,
                    arrival,
                    clockTime,
                    m_Runtime.m_SimClock.Snapshot,
                    frame,
                    out MonitorStopResult result))
            {
                MonitorChange average = result.Sample.Line == Entity.Null || !IsRail(result.Line)
                    ? default
                    : m_Averages.Add(result.Sample);
                return new MonitorChange(
                    true,
                    result.Line,
                    result.ServiceDateKey,
                    result.TripKey,
                    average.MonitorRevision,
                    average.MonitorAverageBecameReady);
            }
            return default;
        }

        public MonitorChange Skip(
            Entity vehicle,
            Entity line,
            Entity station,
            int waypointIndex,
            uint frame)
        {
            if (m_Runtime.m_ObsRecorder != null
                && m_Runtime.m_ObsRecorder.Skip(
                    vehicle,
                    line,
                    station,
                    m_StopKey(m_Anchor(station)),
                    waypointIndex,
                    m_Runtime.m_SimClock.Snapshot,
                    frame,
                    out MonitorStopResult result))
            {
                return new MonitorChange(
                    true,
                    result.Line,
                    result.ServiceDateKey,
                    result.TripKey,
                    0,
                    false);
            }
            return default;
        }

        public bool TryMonitorAverageState(
            Entity line,
            string expectedStopSig,
            out MonitorAverageState state)
        {
            return m_Averages.TryState(line, expectedStopSig, out state);
        }

        public bool TryMonitorAverageSnapshot(
            Entity line,
            string expectedStopSig,
            out MonitorAverageSnapshot snapshot)
        {
            return m_Averages.TrySnapshot(line, expectedStopSig, out snapshot);
        }

        private bool IsRail(Entity line)
        {
            return line != Entity.Null
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle == LifecycleKind.Rail;
        }

        public void Hold(
            Entity vehicle,
            Entity blocker,
            Entity holdStation,
            int waypointIndex,
            uint nowFrame,
            string reasonCode)
        {
            m_Runtime.m_ObsRecorder?.Hold(vehicle, blocker, holdStation, waypointIndex, nowFrame, reasonCode);
        }

        public void Release(Entity vehicle, Entity blocker, uint nowFrame, string releaseReason)
        {
            m_Runtime.m_ObsRecorder?.Release(vehicle, blocker, nowFrame, releaseReason);
        }

        public void EndMonitor(Entity vehicle, uint frame, MonitorEndReason reason)
        {
            if (m_Runtime.m_ObsRecorder != null)
                m_Runtime.m_ObsRecorder.End(vehicle, frame, reason);
        }

        public void SuppressMonitor(
            Entity vehicle,
            string stopSig,
            int[] waypointIndices,
            uint frame)
        {
            if (m_Runtime.m_ObsRecorder != null)
                m_Runtime.m_ObsRecorder.ReprojectPlan(
                    vehicle,
                    stopSig,
                    waypointIndices,
                    frame);
        }

        public void ReleaseLineMonitor(Entity line, uint frame)
        {
            if (m_Runtime.m_ObsRecorder == null)
                return;
            m_Runtime.m_ObsRecorder.ReleaseLinePlan(line, frame);
        }

        internal void RestoreMonitorClaims(
            IReadOnlyList<MonitorClaimSeed> seeds,
            ClockSnapshot clock)
        {
            m_Runtime.m_ObsRecorder?.RestoreMonitorClaims(seeds, clock);
        }

        public void MarkMissed(
            IReadOnlyList<DispatchScheduler.MissedCandidate> candidates,
            uint frame)
        {
            if (candidates == null || candidates.Count == 0 || m_Runtime.m_ObsRecorder == null)
                return;
            m_Runtime.m_ObsRecorder.TickDate(m_Runtime.m_SimClock.NowDate);
            for (int i = 0; i < candidates.Count; i++)
            {
                DispatchScheduler.MissedCandidate candidate = candidates[i];
                m_Runtime.m_ObsRecorder.MarkMissed(
                    candidate.Line,
                    candidate.Row,
                    candidate.ServiceDate,
                    candidate.Final,
                    frame);
            }
        }

        public void TickMonitor(DateTime currentDate)
        {
            m_Runtime.m_ObsRecorder?.TickDate(currentDate);
        }

        internal IEnumerable<MonitorTrip> ActiveMonitorTrips =>
            m_Runtime.m_ObsRecorder?.ActiveMonitorTrips ?? Array.Empty<MonitorTrip>();

        internal IEnumerable<MonitorDateSlot> MonitorDateSlots =>
            m_Runtime.m_ObsRecorder?.MonitorDateSlots ?? Array.Empty<MonitorDateSlot>();

        public int TargetMinute(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return -1;
            if (m_Runtime.m_VehicleStateStore.CurrentSlotMinute.IsCreated && m_Runtime.m_VehicleView.TryGetSlot(vehicle, out int currentSlotMinute))
                return currentSlotMinute;
            if (m_Runtime.m_VehicleStateStore.TargetMinute.IsCreated && m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute))
                return targetMinute;
            return -1;
        }

        public bool IsWaitingOriginDwell(Entity vehicle, uint nowFrame)
        {
            return m_Runtime.m_VehicleView.TryGetReady(vehicle, out uint readyFrame) && nowFrame < readyFrame;
        }

        public void ClearForcedMidStop(Entity vehicle)
        {
            m_Runtime.m_StopRuntime.ClearForcedMidStop(vehicle);
        }

        public void ClearStationAnchorObservationDiagnosticsState()
        {
            m_Runtime.m_LastStationStopDwellLegacyBufferCount = 0;
            m_Runtime.m_LastStationStopDwellLegacyRestoredCount = 0;
            m_Runtime.m_LastStationStopDwellAnchorBufferCount = 0;
            m_Runtime.m_LastStationStopDwellAnchorRestoredCount = 0;
            m_Runtime.m_StationAnchorObservationDiagLastLogFrame = 0;
            m_Runtime.m_StationAnchorDiagAcceptedSamples = 0;
            m_Runtime.m_StationAnchorDiagLegacyWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousLongDwell = 0;
            m_Runtime.m_StationAnchorDiagTotalAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell = 0;
        }

        private bool RecordStationDwellObservation(
            Entity line,
            int waypointIndex,
            float sampleFrames,
            uint nowFrame,
            float sampleMinutes)
        {
            m_Runtime.m_StationAnchorDiagAcceptedSamples++;
            bool suspiciousOriginOrTerminal = IsSuspiciousOriginOrTerminalStationStopDwellSample(line, waypointIndex);
            if (suspiciousOriginOrTerminal)
            {
                m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal++;
                m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal++;
            }
            if (sampleMinutes > ModRuntimeHostSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES)
            {
                m_Runtime.m_StationAnchorDiagSuspiciousLongDwell++;
                m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell++;
            }

            if (!DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return false;
            }

            if (suspiciousOriginOrTerminal)
            {
                m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal++;
                m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return false;
            }

            string observationKey = DwellKey(line, anchor.StationAnchorId);
            if (string.IsNullOrWhiteSpace(observationKey))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return false;
            }

            m_Capture.RecordStationDwellObservation(observationKey, sampleFrames, nowFrame);
            m_Runtime.m_StationAnchorDiagAnchorWritten++;
            MaybeLogStationAnchorObservationDiagnostics(nowFrame);
            return true;
        }

        private bool IsSuspiciousOriginOrTerminalStationStopDwellSample(Entity line, int waypointIndex)
        {
            if (line == Entity.Null || waypointIndex < 0 || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            return waypointIndex == 0;
        }

        private void MaybeLogStationAnchorObservationDiagnostics(uint nowFrame)
        {
            if (m_Runtime.m_StationAnchorObservationDiagLastLogFrame != 0
                && nowFrame - m_Runtime.m_StationAnchorObservationDiagLastLogFrame < ModRuntimeHostSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES)
            {
                return;
            }

            if (m_Runtime.m_StationAnchorDiagAcceptedSamples == 0
                && m_Runtime.m_StationAnchorDiagLegacyWritten == 0
                && m_Runtime.m_StationAnchorDiagAnchorWritten == 0
                && m_Runtime.m_StationAnchorDiagAnchorMissing == 0)
            {
                m_Runtime.m_StationAnchorObservationDiagLastLogFrame = nowFrame;
                return;
            }

            if (!RtLog.VerboseEnabled)
            {
                m_Runtime.m_StationAnchorObservationDiagLastLogFrame = nowFrame;
                return;
            }

            StationAnchorObservationDiagnosticsDto diagnostics = m_Runtime.m_StationAnchorDiagnostics.Build();
            StationAnchorObservationSummaryDto coverage = diagnostics.summary;
            string standaloneStops = string.Join(",", diagnostics.anchorGroups
                .Where(group => group.buildingEntityIndex < 0)
                .SelectMany(group => group.stopEntityIndices)
                .Distinct()
                .OrderBy(entityIndex => entityIndex));
            string attachedStops = string.Join(",", diagnostics.anchorGroups
                .Where(group => group.buildingEntityIndex >= 0)
                .SelectMany(group => group.stopEntityIndices.Select(
                    stopEntityIndex => stopEntityIndex + "->" + group.buildingEntityIndex))
                .Distinct()
                .OrderBy(mapping => mapping, StringComparer.Ordinal));
            m_Runtime.log.Info("[StationAnchorDiag] intervalFrames=" + ModRuntimeHostSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " lines=" + coverage.lineCount
                + " stopWaypoints=" + coverage.stopWaypointCount
                + " anchorResolved=" + coverage.anchorResolvedCount
                + " anchorMissing=" + coverage.anchorMissingCount
                + " uniqueAnchors=" + coverage.uniqueAnchorCount
                + " duplicateAnchorOccurrences=" + coverage.duplicateAnchorOccurrenceCount
                + " standaloneStops=[" + standaloneStops + "]"
                + " attachedStops=[" + attachedStops + "]");

            m_Runtime.log.Info("[StopDwellAnchorDiag] intervalFrames=" + ModRuntimeHostSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " accepted=" + m_Runtime.m_StationAnchorDiagAcceptedSamples
                + " legacyWritten=" + m_Runtime.m_StationAnchorDiagLegacyWritten
                + " anchorWritten=" + m_Runtime.m_StationAnchorDiagAnchorWritten
                + " anchorMissing=" + m_Runtime.m_StationAnchorDiagAnchorMissing
                + " anchorRejectedOriginOrTerminal=" + m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal
                + " uniqueAnchors=" + m_Runtime.m_ObsQuery.StationDwellCount
                + " suspiciousOriginOrTerminal=" + m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal
                + " suspiciousLongDwell=" + m_Runtime.m_StationAnchorDiagSuspiciousLongDwell);

            m_Runtime.m_StationAnchorObservationDiagLastLogFrame = nowFrame;
            m_Runtime.m_StationAnchorDiagAcceptedSamples = 0;
            m_Runtime.m_StationAnchorDiagLegacyWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousLongDwell = 0;
        }

        private static EchoDto CopyEcho(DispatchPlannerRequestEchoDto source)
        {
            if (source == null)
                return null;

            return new EchoDto
            {
                draftKey = source.draftKey,
                analysisWindowId = source.analysisWindowId,
                windowStart = source.windowStart,
                windowEnd = source.windowEnd,
                localLineIds = source.localLineIds,
                adjustableLineIds = source.adjustableLineIds,
                expressSourceMode = source.expressSourceMode,
                expressLineId = source.expressLineId,
                virtualExpressBaseLineId = source.virtualExpressBaseLineId,
                expressStopStationIds = source.expressStopStationIds,
                departureMode = source.departureMode,
                expressTripsPerHour = source.expressTripsPerHour,
                intervalMinutes = source.intervalMinutes,
                phaseTime = source.phaseTime,
                expressOffsetMinutes = source.expressOffsetMinutes,
                maxOffsetMinutes = source.maxOffsetMinutes,
                offsetStepMinutes = source.offsetStepMinutes,
                maxLocalRetimeMinutes = source.maxLocalRetimeMinutes,
                maxLocalWaitMinutes = source.maxLocalWaitMinutes,
                maxAdditionalBypassStations = source.maxAdditionalBypassStations,
                forcedBypassStationIds = source.forcedBypassStationIds
            };
        }

        private static RoleSummaryDto CopyRoleSummary(DispatchPlannerLineRoleSummaryDto source)
        {
            if (source == null)
                return null;

            return new RoleSummaryDto
            {
                effectiveLineIds = source.effectiveLineIds,
                adjustableLineIds = source.adjustableLineIds,
                fixedLineIds = source.fixedLineIds,
                targetLineIds = source.targetLineIds,
                autoFixedConstraintLineIds = source.autoFixedConstraintLineIds,
                suppressedFixedVsFixedClusterCount = source.suppressedFixedVsFixedClusterCount,
                roles = (source.roles ?? Array.Empty<DispatchPlannerLineRoleDto>())
                    .Select(CopyRole)
                    .ToArray()
            };
        }

        private static RoleDto CopyRole(DispatchPlannerLineRoleDto source)
        {
            if (source == null)
                return null;

            return new RoleDto
            {
                lineId = source.lineId,
                participates = source.participates,
                adjustable = source.adjustable,
                fixedLine = source.fixedLine,
                target = source.target
            };
        }

        private static ChangeDto CopyChange(DispatchPlannerChangedRowDto source)
        {
            if (source == null)
                return null;

            return new ChangeDto
            {
                tripId = source.tripId,
                lineId = source.lineId,
                kind = source.kind,
                beforeTime = source.beforeTime,
                afterTime = source.afterTime,
                scheduleShiftMinutes = source.scheduleShiftMinutes,
                predictedDelayMinutes = source.predictedDelayMinutes,
                totalDeltaMinutes = source.totalDeltaMinutes,
                changeType = source.changeType,
                statusCode = source.statusCode,
                statusMinutes = source.statusMinutes
            };
        }

        private static ActionDto CopyAction(DispatchPlannerScheduleActionDto source)
        {
            if (source == null)
                return null;

            return new ActionDto
            {
                actionType = source.actionType,
                type = source.type,
                shape = source.shape,
                reason = source.reason,
                targetRegionIds = source.targetRegionIds,
                reasonRegionIds = source.reasonRegionIds,
                clusterIds = source.clusterIds,
                reasonClusterIds = source.reasonClusterIds,
                stationIds = source.stationIds,
                affectedLineIds = source.affectedLineIds,
                affectedLineId = source.affectedLineId,
                affectedTripIds = source.affectedTripIds,
                priorityTripIds = source.priorityTripIds,
                tripIds = source.tripIds,
                deltaPattern = source.deltaPattern,
                deltaMinutes = source.deltaMinutes,
                deltaOffsetMinutes = source.deltaOffsetMinutes,
                riskScore = source.riskScore
            };
        }

        private static RiskDto CopyRisk(DispatchPlannerRiskItemDto source)
        {
            if (source == null)
                return null;

            return new RiskDto
            {
                riskId = source.riskId,
                problemType = source.problemType,
                resolutionState = source.resolutionState,
                pairRole = source.pairRole,
                treatmentType = source.treatmentType,
                blockReasonCode = source.blockReasonCode,
                suggestedOptionCodes = source.suggestedOptionCodes,
                yieldingLineId = source.yieldingLineId,
                priorityLineId = source.priorityLineId,
                yieldingTripId = source.yieldingTripId,
                priorityTripId = source.priorityTripId,
                yieldingDepartTime = source.yieldingDepartTime,
                priorityDepartTime = source.priorityDepartTime,
                fromStationId = source.fromStationId,
                toStationId = source.toStationId,
                catchupFromStationId = source.catchupFromStationId,
                catchupToStationId = source.catchupToStationId,
                catchupTime = source.catchupTime,
                selectedBypassStationId = source.selectedBypassStationId,
                requiredHoldMinutes = source.requiredHoldMinutes,
                plannedAdjustmentMinutes = source.plannedAdjustmentMinutes,
                holdBudgetMinutes = source.holdBudgetMinutes,
                unresolvedRiskMinutes = source.unresolvedRiskMinutes,
                robustnessRiskMinutes = source.robustnessRiskMinutes,
                requiredMarginMinutes = source.requiredMarginMinutes,
                currentWorstCaseGapMinutes = source.currentWorstCaseGapMinutes
            };
        }
    }
}
