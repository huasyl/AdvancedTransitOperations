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
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class ObservationPort
    {
        private const float DispatchSampleOutlierFactor = 1.5f;
        private const uint DISPATCH_SAMPLE_MIN_FRAMES = 365u;

        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Capture m_Capture;
        private readonly SliceAdmission m_Admission;
        private readonly Dictionary<Entity, DwellDeadlineCacheEntry> m_DwellDeadlineCache =
            new Dictionary<Entity, DwellDeadlineCacheEntry>();
        private readonly Dictionary<Entity, DispatchEtaRequest> m_DispatchEtaRequests =
            new Dictionary<Entity, DispatchEtaRequest>();

        private sealed class DispatchEtaRequest
        {
            public uint DispatchFrame;
        }

        private readonly struct DwellDeadlineCacheEntry
        {
            public readonly Entity Line;
            public readonly int WaypointIndex;
            public readonly uint DwellSinceFrame;
            public readonly int MaxDwellMinutes;
            public readonly ulong ConfigVersion;
            public readonly uint DeadlineFrame;

            public DwellDeadlineCacheEntry(
                Entity line,
                int waypointIndex,
                uint dwellSinceFrame,
                int maxDwellMinutes,
                ulong configVersion,
                uint deadlineFrame)
            {
                Line = line;
                WaypointIndex = waypointIndex;
                DwellSinceFrame = dwellSinceFrame;
                MaxDwellMinutes = maxDwellMinutes;
                ConfigVersion = configVersion;
                DeadlineFrame = deadlineFrame;
            }
        }

        public ObservationPort(DispatchRuntimeSystem runtime, Capture capture, SliceAdmission admission)
        {
            m_Runtime = runtime;
            m_Capture = capture;
            m_Admission = admission;
            m_Runtime.m_SimClock.ClockChanged += OnClockChanged;
        }

        private void OnClockChanged(ClockSnapshot oldClockSnapshot, ClockSnapshot newClockSnapshot)
        {
            _ = oldClockSnapshot;
            _ = newClockSnapshot;
            m_DwellDeadlineCache.Clear();
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

        public bool ShouldSample(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            return m_Capture.ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, nowFrame);
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

        public bool Dwell(
            Entity vehicle,
            Entity line,
            int currentWaypointIndex,
            bool boarding,
            uint nowFrame,
            int waypointCount,
            out uint dwellSinceFrame,
            out uint dwellDeadlineFrame,
            out int maxDwellMinutes)
        {
            dwellSinceFrame = 0;
            dwellDeadlineFrame = 0;
            maxDwellMinutes = m_Runtime.m_LineView.Dwell(line);
            if (!boarding || currentWaypointIndex <= 0 || currentWaypointIndex >= waypointCount)
            {
                ClearDwellDeadlineCache(vehicle);
                if (m_Runtime.m_ObsPersist.RemoveDwellStart(vehicle))
                {
                    ClearForcedMidStop(vehicle);
                    if (RtLog.VerboseEnabled)
                    {
                        m_Runtime.log.Info("[StopDwellEnd] line" + line.Index
                            + " vehicle" + vehicle.Index
                            + " boarding=" + boarding
                            + " wp=" + currentWaypointIndex
                            + "/" + (waypointCount - 1)
                            + " nowFrame=" + nowFrame);
                    }
                }
                return false;
            }

            if (maxDwellMinutes <= 0)
                return false;

            if (!m_Runtime.m_ObsQuery.TryDwellStart(vehicle, out dwellSinceFrame))
            {
                dwellSinceFrame = nowFrame;
                m_Runtime.m_ObsPersist.SetDwellStart(vehicle, dwellSinceFrame);
                dwellDeadlineFrame = GetDwellDeadline(vehicle, line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
                if (RtLog.VerboseEnabled)
                {
                    m_Runtime.log.Info("[StopDwellBegin] line" + line.Index
                        + " vehicle" + vehicle.Index
                        + " wp=" + currentWaypointIndex
                        + "/" + (waypointCount - 1)
                        + " limit=" + maxDwellMinutes + "min"
                        + " deadlineFrame=" + dwellDeadlineFrame);
                }
                return false;
            }

            dwellDeadlineFrame = GetDwellDeadline(vehicle, line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
            return nowFrame >= dwellDeadlineFrame;
        }

        public void ClearDwellDeadlineCache(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_DwellDeadlineCache.Remove(vehicle);
        }

        public void ClearDwellDeadlineCache()
        {
            m_DwellDeadlineCache.Clear();
        }

        public uint ComputeAdjustedStopDwellDeadlineFrame(
            Entity line,
            int waypointIndex,
            uint dwellSinceFrame,
            int maxDwellMinutes)
        {
            ClockSnapshot clockSnapshot = m_Runtime.m_SimClock.Snapshot;
            float configuredFrames = clockSnapshot.ToFramesCeil(maxDwellMinutes);
            float earlyCloseFrames = 0f;

            if (line != Entity.Null
                && waypointIndex >= 0
                && TryGetObservedWaypointStopFrames(line, waypointIndex, out float observationFrames)
                && observationFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observationFrames - configuredFrames,
                    clockSnapshot.ToFramesCeil(DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES));
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
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

        public bool TryEstimateRemainingBoardingTime(
            Entity vehicle,
            Entity line,
            int currentWaypointIndex,
            uint nowFrame,
            out float remainingFrames)
        {
            remainingFrames = 0f;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentWaypointIndex <= 0
                || !m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                return false;
            }

            if ((m_Runtime.EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            if (!m_Runtime.m_ObsQuery.TryDwellStart(vehicle, out uint dwellSinceFrame) || nowFrame <= dwellSinceFrame)
                return false;

            float elapsedFrames = nowFrame - dwellSinceFrame;
            if (TryGetObservedWaypointStopFrames(line, currentWaypointIndex, out float observedDwellFrames)
                && observedDwellFrames > 0f)
            {
                remainingFrames = math.max(0f, observedDwellFrames - elapsedFrames);
                return remainingFrames > 0f;
            }

            int maxStationDwellMinutes = m_Runtime.m_LineView.Dwell(line);
            if (maxStationDwellMinutes <= 0)
                return false;

            ClockSnapshot clockSnapshot = m_Runtime.m_SimClock.Snapshot;
            float configuredFrames = clockSnapshot.ToFramesCeil(maxStationDwellMinutes);
            remainingFrames = math.max(0f, configuredFrames - elapsedFrames);
            return remainingFrames > 0f;
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

        public void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
        {
            if (!m_Capture.TryRecordObservedStopDwellOnBoardingEnd(vehicle, line, fallbackWaypointIndex, nowFrame, out ObservedDwellSample sample))
                return;

            RecordStationDwellObservation(sample.Line, sample.WaypointIndex, sample.SampleFrames, sample.Frame, sample.SampleMinutes);
            m_Runtime.m_Bypass.ExpireLine(sample.Line);
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
            bool hasSample = false;
            if (m_Runtime.m_VehicleView.TryGetDispatch(vehicle, out uint dispatchRequestStart))
            {
                sampleFrames = nowFrame - dispatchRequestStart;
                hasSample = true;
            }
            else if (m_Runtime.m_VehicleView.TryGetPreparing(vehicle, out uint prepStart))
            {
                sampleFrames = nowFrame - prepStart;
                hasSample = true;
            }

            m_Runtime.m_VehicleRegistry.ClearPreparing(vehicle);
            m_Runtime.m_VehicleRegistry.ClearDispatch(vehicle);
            m_DispatchEtaRequests.Remove(vehicle);
            if (!hasSample || sampleFrames == 0)
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
            if (cachedFrames > 0f && sampleFrames > cachedFrames * DispatchSampleOutlierFactor)
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
            if (vehicle == Entity.Null || m_DispatchEtaRequests.ContainsKey(vehicle))
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
        }

        public string Json()
        {
            return m_Runtime.m_ObsRecorder?.SnapshotJson() ?? string.Empty;
        }

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

        public void Launch(Entity line, Entity vehicle, int targetMinute, int actualMinute, uint launchFrame, bool lateDispatch)
        {
            m_Admission.Begin(line, vehicle, targetMinute);
            m_Runtime.m_ObsRecorder?.Launch(line, vehicle, targetMinute, actualMinute, launchFrame, lateDispatch);
        }

        public void Stop(
            Entity vehicle,
            Entity line,
            Entity station,
            ResolvedStopKind kind,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            string clockTime,
            uint frame)
        {
            m_Runtime.m_ObsRecorder?.Stop(vehicle, line, station, kind, waypointIndex, isOrigin, arrival, clockTime, frame);
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
            if (vehicle == Entity.Null)
                return;

            m_Runtime.m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_Runtime.m_RuntimeLog.m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        public bool IsSuppressedMidStopGhost(
            Entity vehicle,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out int targetWaypointIndex)
        {
            targetWaypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_Runtime.m_ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil))
            {
                return false;
            }

            if (nowFrame >= graceUntil)
            {
                m_Runtime.m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
                return false;
            }

            if (!m_Runtime.EntityManager.HasComponent<Waypoint>(target.m_Target))
                return false;

            targetWaypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypointIndex < 0 || targetWaypointIndex >= waypoints.Length)
                return false;

            Entity targetStop = GetConnectedStopForWaypoint(waypoints[targetWaypointIndex].m_Waypoint);
            if (targetStop == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(targetStop)
                || m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle != vehicle
                || !m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(targetStop)
                || !m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            float3 vehiclePosition = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPosition = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(targetStop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > DispatchRuntimeSystem.AT_STOP_MAX_DIST;
        }

        private uint ComputeDeadline(Entity line, int waypointIndex, uint dwellSinceFrame, int maxDwellMinutes)
        {
            ClockSnapshot clockSnapshot = m_Runtime.m_SimClock.Snapshot;
            float configuredFrames = clockSnapshot.ToFramesCeil(maxDwellMinutes);
            float earlyCloseFrames = 0f;

            if (line != Entity.Null
                && waypointIndex >= 0
                && TryGetObservedWaypointStopFrames(line, waypointIndex, out float observationFrames)
                && observationFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observationFrames - configuredFrames,
                    clockSnapshot.ToFramesCeil(DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES));
            }

            if (!(earlyCloseFrames > 0f)
                && DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor)
                && m_Capture.TryGetObservedWaypointStopFrames(line, waypointIndex, anchor.StationAnchorId, out float anchoredFrames)
                && anchoredFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    anchoredFrames - configuredFrames,
                    clockSnapshot.ToFramesCeil(DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES));
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
        }

        private uint GetDwellDeadline(
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint dwellSinceFrame,
            int maxDwellMinutes)
        {
            ulong cfgVersion = m_Runtime.m_LineView.CfgVersion();
            if (vehicle != Entity.Null
                && m_DwellDeadlineCache.TryGetValue(vehicle, out DwellDeadlineCacheEntry entry)
                && entry.Line == line
                && entry.WaypointIndex == waypointIndex
                && entry.DwellSinceFrame == dwellSinceFrame
                && entry.MaxDwellMinutes == maxDwellMinutes
                && entry.ConfigVersion == cfgVersion)
            {
                m_Runtime.m_RuntimeHotPathProbe.CountDwellDeadlineCacheHit();
                return entry.DeadlineFrame;
            }

            m_Runtime.m_RuntimeHotPathProbe.CountDwellDeadlineCacheMiss();
            uint deadlineFrame = ComputeDeadline(line, waypointIndex, dwellSinceFrame, maxDwellMinutes);
            if (vehicle != Entity.Null)
            {
                m_DwellDeadlineCache[vehicle] = new DwellDeadlineCacheEntry(
                    line,
                    waypointIndex,
                    dwellSinceFrame,
                    maxDwellMinutes,
                    cfgVersion,
                    deadlineFrame);
            }
            return deadlineFrame;
        }

        private Entity GetConnectedStopForWaypoint(Entity waypoint)
        {
            if (waypoint == Entity.Null
                || !m_Runtime.EntityManager.Exists(waypoint)
                || !m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
            {
                return Entity.Null;
            }

            Entity connected = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return connected != Entity.Null && m_Runtime.EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
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

        private void RecordStationDwellObservation(
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
            if (sampleMinutes > DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES)
            {
                m_Runtime.m_StationAnchorDiagSuspiciousLongDwell++;
                m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell++;
            }

            if (!DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            if (suspiciousOriginOrTerminal)
            {
                m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal++;
                m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            string observationKey = DwellKey(line, anchor.StationAnchorId);
            if (string.IsNullOrWhiteSpace(observationKey))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            m_Capture.RecordStationDwellObservation(observationKey, sampleFrames, nowFrame);
            ClearDwellDeadlineCache();
            m_Runtime.m_StationAnchorDiagAnchorWritten++;
            MaybeLogStationAnchorObservationDiagnostics(nowFrame);
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
                && nowFrame - m_Runtime.m_StationAnchorObservationDiagLastLogFrame < DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES)
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

            StationAnchorObservationSummaryDto coverage = m_Runtime.m_StationAnchorDiagnostics.Build().summary;
            m_Runtime.log.Info("[StationAnchorDiag] intervalFrames=" + DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " lines=" + coverage.lineCount
                + " stopWaypoints=" + coverage.stopWaypointCount
                + " anchorResolved=" + coverage.anchorResolvedCount
                + " anchorMissing=" + coverage.anchorMissingCount
                + " uniqueAnchors=" + coverage.uniqueAnchorCount
                + " duplicateAnchorOccurrences=" + coverage.duplicateAnchorOccurrenceCount);

            m_Runtime.log.Info("[StopDwellAnchorDiag] intervalFrames=" + DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
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
