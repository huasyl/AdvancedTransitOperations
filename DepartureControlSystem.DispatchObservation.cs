using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void RecordLapStart(Entity v, string reason = "")
        {
            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "line" + le.Index : "line?";
            if (!EntityManager.HasComponent<Odometer>(v))
            {
                log.Info("[LapStartSkip] " + lineTag + " vehicle" + v.Index
                    + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                    + " no-odometer");
                return;
            }

            float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            uint nowFrame = m_SimulationSystem.frameIndex;
            m_VehicleLapStartOdometer[v] = currentOdo;
            m_VehicleLapStartFrame[v] = nowFrame;
            string curSlot = m_VehicleCurrentSlot.TryGetValue(v, out int cs) ? SlotStr(cs) : "-";
            int cachedWp = m_CachedWpIdx.TryGetValue(v, out int cw) ? cw : -1;
            log.Info("[LapStart] " + lineTag + " vehicle" + v.Index
                + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                + " frame=" + nowFrame
                + " odo=" + currentOdo.ToString("F1")
                + " curSlot=" + curSlot
                + " cachedWp=" + cachedWp);
        }

        private void UpdateLapStats(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v))
                return;
            if (!m_VehicleLapStartOdometer.TryGetValue(v, out float startOdo))
                return;

            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            float lapDist = current - startOdo;
            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "line" + le.Index : "line?";

            if (m_RestoredRunning.Contains(v))
            {
                m_RestoredRunning.Remove(v);
                if (lapDist > 0f)
                    m_VehicleLapDistance[v] = lapDist;
                ClearVehicleTraversalSliceLapDebug(v);
                log.Info("[LapStatsSkipRestored] " + lineTag + " vehicle" + v.Index
                    + " lapDist=" + (lapDist / 1000f).ToString("F2") + "km"
                    + " restored-first-lap skip-lap-time-write");
                return;
            }

            if (lapDist > 0f)
            {
                m_VehicleLapDistance[v] = lapDist;
                float maintenanceRange = 0f;
                if (EntityManager.HasComponent<PrefabRef>(v))
                {
                    Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
                    if (EntityManager.HasComponent<PublicTransportVehicleData>(pref))
                        maintenanceRange = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
                }

                float remaining = maintenanceRange > 0f ? (maintenanceRange - current) : -1f;
                string maintStr = maintenanceRange > 0f
                    ? (" maintenance=" + (maintenanceRange / 1000f).ToString("F1") + "km remaining=" + (remaining / 1000f).ToString("F1") + "km")
                    : " maintenance=none";
                log.Info("[LapDistance] " + lineTag + " vehicle" + v.Index
                    + " lap=" + (lapDist / 1000f).ToString("F2") + "km" + maintStr);
            }

            if (m_VehicleLapStartFrame.TryGetValue(v, out uint startFrame))
            {
                uint framesDelta = m_SimulationSystem.frameIndex - startFrame;
                m_VehicleLapFrames[v] = framesDelta;
                float realMin = framesDelta / (float)SIM_FRAMES_PER_MINUTE;
                log.Info("[LapStats] " + lineTag + " vehicle" + v.Index
                    + " lap=" + realMin.ToString("F1") + "min/" + framesDelta + "frames");

                if (m_VehicleLine.TryGetValue(v, out Entity timingLine)
                    && timingLine != Entity.Null
                    && IsAppliedWorkbenchExpressLine(timingLine)
                    && EntityManager.HasBuffer<RouteWaypoint>(timingLine))
                {
                    DynamicBuffer<RouteWaypoint> timingWaypoints = EntityManager.GetBuffer<RouteWaypoint>(timingLine, true);
                    if (TryGetTraversalProfileLapTiming(
                            timingLine,
                            timingWaypoints,
                            out float profileRunFrames,
                            out float profileStopFrames,
                            out int profileStopCount,
                            out int profilePassCount))
                    {
                        float profileTotalFrames = profileRunFrames + profileStopFrames;
                        log.Info("[ExpressLapProfile] " + lineTag + " vehicle" + v.Index
                            + " observed=" + realMin.ToString("F1") + "min"
                            + " profileTotal=" + (profileTotalFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min"
                            + " run=" + (profileRunFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min"
                            + " stop=" + (profileStopFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min"
                            + " stopCount=" + profileStopCount
                            + " passCount=" + profilePassCount);
                        LogTraversalProfileLapSlices(v, timingLine, timingWaypoints);
                    }
                }

                if (m_VehicleLine.TryGetValue(v, out Entity lapLine))
                    FlushLineLapCache(lapLine);

                ClearVehicleTraversalSliceLapDebug(v);
            }
        }

        private bool TryGetTraversalProfileLapTiming(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out float runFrames,
            out float stopFrames,
            out int stopCount,
            out int passCount)
        {
            runFrames = 0f;
            stopFrames = 0f;
            stopCount = 0;
            passCount = 0;

            if (line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            EnsureTrackChainBypassPipelineReady(chain);
            if (chain.TraversalProfile == null)
                return false;

            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                if (TryGetEffectiveTraversalRunSliceFrames(line, chain.TraversalProfile.RunSlices[i], out float effectiveRunFrames))
                    runFrames += math.max(0f, effectiveRunFrames);
            }

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop)
                {
                    stopCount++;
                    stopFrames += math.max(0f, traversalEvent.StopFrames);
                }
                else if (traversalEvent.Kind == TraversalEventKind.Pass)
                {
                    passCount++;
                }
            }

            return runFrames > 0f || stopFrames > 0f || stopCount > 0 || passCount > 0;
        }

        private void UpdateVehicleTraversalSliceObservation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            if (!TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, out int sliceIndex, out VehicleTrackCursor cursor))
            {
                if (m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession droppedSession))
                    RecordTraversalSliceLapDebugDropped(vehicle, droppedSession.SliceIndex);
                m_VehicleTraversalSliceSessions.Remove(vehicle);
                return;
            }

            if (m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                && session.Line == line
                && session.SliceIndex == sliceIndex)
            {
                return;
            }

            FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame);
            if (TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                && chain.TraversalProfile != null
                && sliceIndex >= 0
                && sliceIndex < chain.TraversalProfile.RunSlices.Count)
            {
                RecordTraversalSliceLapDebugStart(vehicle, chain.TraversalProfile.RunSlices[sliceIndex], cursor.AtomCursorIndex, cursor.AtomPosition01);
            }

            m_VehicleTraversalSliceSessions[vehicle] = new VehicleTraversalSliceSession(line, sliceIndex, nowFrame, cursor.AtomCursorIndex, cursor.AtomPosition01);
        }

        private void FinalizeVehicleTraversalSliceObservation(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line == Entity.Null
                || session.SliceIndex < 0
                || nowFrame <= session.EnterFrame)
            {
                m_VehicleTraversalSliceSessions.Remove(vehicle);
                return;
            }

            float observedFrames = nowFrame - session.EnterFrame;
            RecordTraversalSliceLapDebugFinalize(vehicle, session.SliceIndex, observedFrames);
            ulong key = MakeTraversalSliceObservationKey(session.Line, session.SliceIndex);
            if (m_TraversalRunSliceObservations.TryGetValue(key, out TraversalSliceObservation existing))
            {
                int sampleCount = existing.SampleCount + 1;
                float averageFrames = ((existing.AverageFrames * existing.SampleCount) + observedFrames) / sampleCount;
                float fastBaselineFrames = ComputeFastTraversalBaselineFrames(existing.FastBaselineFrames, observedFrames);
                TraversalSliceObservation updated = new TraversalSliceObservation(averageFrames, fastBaselineFrames, sampleCount, nowFrame);
                m_TraversalRunSliceObservations[key] = updated;
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, updated);
            }
            else
            {
                TraversalSliceObservation created = new TraversalSliceObservation(observedFrames, observedFrames, 1, nowFrame);
                m_TraversalRunSliceObservations[key] = created;
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, created);
            }

            m_VehicleTraversalSliceSessions.Remove(vehicle);
        }

        private static float ComputeFastTraversalBaselineFrames(float existingFastBaselineFrames, float observedFrames)
        {
            if (!(observedFrames > 0f))
                return existingFastBaselineFrames;

            if (!(existingFastBaselineFrames > 0f))
                return observedFrames;

            const float fastFollowAlpha = 0.35f;
            const float slowFollowAlpha = 0.05f;
            float alpha = observedFrames <= existingFastBaselineFrames
                ? fastFollowAlpha
                : slowFollowAlpha;
            return math.lerp(existingFastBaselineFrames, observedFrames, alpha);
        }

        private bool TryGetCurrentTraversalRunSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            sliceIndex = -1;
            cursor = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
            {
                return false;
            }

            int atomIndex = math.clamp(cursor.AtomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[i];
                if (atomIndex >= slice.StartAtomIndex && atomIndex < slice.EndAtomIndexExclusive)
                {
                    sliceIndex = i;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetEffectiveTraversalRunSliceFrames(
            Entity line,
            TraversalRunSlice slice,
            out float effectiveRunFrames)
        {
            effectiveRunFrames = math.max(0f, slice.RunFrames);
            if (line == Entity.Null || slice.SliceIndex < 0)
                return effectiveRunFrames > 0f;

            ulong key = MakeTraversalSliceObservationKey(line, slice.SliceIndex);
            if (m_TraversalRunSliceObservations.TryGetValue(key, out TraversalSliceObservation observation)
                && observation.SampleCount > 0
                && observation.FastBaselineFrames > 0f)
            {
                effectiveRunFrames = observation.FastBaselineFrames;
                return true;
            }

            return effectiveRunFrames > 0f;
        }

        private static ulong MakeTraversalSliceObservationKey(Entity line, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)sliceIndex;
            }
        }

        private static ulong MakeVehicleTraversalSliceLapDebugKey(Entity vehicle, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)vehicle.Index << 32) | (uint)sliceIndex;
            }
        }

        private void RecordTraversalSliceLapDebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
        {
            if (vehicle == Entity.Null || slice.SliceIndex < 0)
                return;

            float enterCoordinate = atomIndex + math.saturate(atomPosition01);
            float enterOffsetAtoms = math.max(0f, enterCoordinate - slice.StartAtomIndex);
            bool midSliceStart = enterOffsetAtoms > 0.05f;
            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, slice.SliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordStart(enterOffsetAtoms, midSliceStart);
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugDropped(Entity vehicle, int sliceIndex)
        {
            if (vehicle == Entity.Null || sliceIndex < 0)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.DroppedWithoutFinalizeCount++;
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugFinalize(Entity vehicle, int sliceIndex, float observedFrames)
        {
            if (vehicle == Entity.Null || sliceIndex < 0 || observedFrames <= 0f)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordFinalize(observedFrames);
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void ClearVehicleTraversalSliceLapDebug(Entity vehicle)
        {
            if (vehicle == Entity.Null || m_VehicleTraversalSliceLapDebug.Count == 0)
                return;

            List<ulong> removeKeys = null;
            foreach (var kvp in m_VehicleTraversalSliceLapDebug)
            {
                if ((int)(kvp.Key >> 32) != vehicle.Index)
                    continue;

                if (removeKeys == null)
                    removeKeys = new List<ulong>();
                removeKeys.Add(kvp.Key);
            }

            if (removeKeys == null)
                return;

            for (int i = 0; i < removeKeys.Count; i++)
                m_VehicleTraversalSliceLapDebug.Remove(removeKeys[i]);
        }

        private void LogTraversalProfileLapSlices(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                return;
            }

            EnsureTrackChainBypassPipelineReady(chain);
            if (chain.TraversalProfile == null || chain.TraversalProfile.RunSlices.Count == 0)
                return;

            string lineTag = "line" + line.Index;
            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[i];
                string startLabel = DescribeTraversalBoundaryLabel(chain, slice.StartEventIndex, slice.StartAtomIndex);
                string endLabel = DescribeTraversalBoundaryLabel(chain, slice.EndEventIndex, slice.EndAtomIndexExclusive);
                float stopFrames = GetTraversalSliceStopFrames(chain, slice);
                float staticRunFrames = math.max(0f, slice.RunFrames);
                TryGetEffectiveTraversalRunSliceFrames(line, slice, out float effectiveRunFrames);
                ulong observationKey = MakeTraversalSliceObservationKey(line, slice.SliceIndex);
                bool hasObservation = m_TraversalRunSliceObservations.TryGetValue(observationKey, out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;
                ulong lapDebugKey = MakeVehicleTraversalSliceLapDebugKey(vehicle, slice.SliceIndex);
                bool hasLapDebug = m_VehicleTraversalSliceLapDebug.TryGetValue(lapDebugKey, out TraversalSliceLapDebugAggregate lapDebug);
                string lapDebugText = string.Empty;
                if (hasLapDebug && lapDebug.StartCount > 0)
                {
                    float avgEnterOffset = lapDebug.EnterOffsetSumAtoms / math.max(1, lapDebug.StartCount);
                    lapDebugText = " lapStart=" + lapDebug.StartCount
                        + " midStart=" + lapDebug.MidSliceStartCount
                        + " drop=" + lapDebug.DroppedWithoutFinalizeCount
                        + " enterOffsetAvg=" + avgEnterOffset.ToString("0.00")
                        + "a"
                        + " enterOffsetMax=" + lapDebug.MaxEnterOffsetAtoms.ToString("0.00")
                        + "a";
                    if (lapDebug.FinalizeCount > 0)
                    {
                        lapDebugText += " obsLapAvg=" + (lapDebug.ObservedFramesSum / lapDebug.FinalizeCount / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                            + " obsLapMin=" + (lapDebug.MinObservedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                            + " obsLapMax=" + (lapDebug.MaxObservedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min";
                    }
                }

                log.Info("[ExpressLapSlices] " + lineTag + " vehicle" + vehicle.Index
                    + " slice#" + slice.SliceIndex
                    + " " + startLabel + " -> " + endLabel
                    + " run=" + (math.max(0f, effectiveRunFrames) / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                    + " staticRun=" + (staticRunFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                    + " stop=" + (stopFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                    + " total=" + ((math.max(0f, effectiveRunFrames) + stopFrames) / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                    + " atoms=" + slice.StartAtomIndex + ".." + slice.EndAtomIndexExclusive
                    + " laneKeys=" + (slice.PhysicalLaneKeys != null ? slice.PhysicalLaneKeys.Length : 0)
                    + " obsGlobal=" + (hasObservation ? observation.SampleCount.ToString() : "0")
                    + (hasObservation
                        ? " obsGlobalAvg=" + (observation.AverageFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                        : string.Empty)
                    + (hasObservation && observation.FastBaselineFrames > 0f
                        ? " obsGlobalFast=" + (observation.FastBaselineFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "min"
                        : string.Empty)
                    + lapDebugText);
            }
        }

        private static float GetTraversalSliceStopFrames(LineTrackChain chain, TraversalRunSlice slice)
        {
            if (chain?.TraversalProfile == null)
                return 0f;

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop
                    && traversalEvent.StartAtomIndex == slice.StartAtomIndex
                    && traversalEvent.EndAtomIndexExclusive == slice.EndAtomIndexExclusive)
                {
                    return math.max(0f, traversalEvent.StopFrames);
                }
            }

            return 0f;
        }

        private string DescribeTraversalBoundaryLabel(LineTrackChain chain, int eventIndex, int atomIndex)
        {
            if (chain != null
                && chain.TraversalProfile != null
                && eventIndex >= 0
                && eventIndex < chain.TraversalProfile.Events.Count)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                bool eventMatchesBoundary = traversalEvent.StartAtomIndex == atomIndex
                    || traversalEvent.EndAtomIndexExclusive == atomIndex;
                if (eventMatchesBoundary)
                {
                    string buildingLabel = traversalEvent.Building != Entity.Null
                        ? FormatBypassNodeLabel(traversalEvent.Building)
                        : "atom" + atomIndex;
                    switch (traversalEvent.Kind)
                    {
                        case TraversalEventKind.Stop:
                            return "Stop(" + buildingLabel + ")";
                        case TraversalEventKind.Pass:
                            return "Pass(" + buildingLabel + ")";
                        case TraversalEventKind.ApproachSplitBoundary:
                            return "ApproachSplit(" + buildingLabel + ")";
                        case TraversalEventKind.DepartureSplitBoundary:
                            return "DepartureSplit(" + buildingLabel + ")";
                    }
                }
            }

            return "atom" + atomIndex;
        }

        private static ulong MakeLineWaypointStopObservationKey(Entity line, int waypointIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)math.max(0, waypointIndex);
            }
        }

        private bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
        {
            dwellFrames = 0f;
            if (line == Entity.Null || waypointIndex < 0)
                return false;

            return m_WaypointStopDwellObservations.TryGetValue(MakeLineWaypointStopObservationKey(line, waypointIndex), out StopDwellObservation observation)
                && observation.AverageFrames > 0f
                && (dwellFrames = observation.AverageFrames) > 0f;
        }

        private bool TryEstimateRemainingBoardingDwellFrames(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            uint nowFrame,
            out float remainingFrames)
        {
            remainingFrames = 0f;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentWaypointIndex <= 0
                || currentWaypointIndex >= waypoints.Length
                || currentBypassBuilding == Entity.Null
                || GetBypassBuildingForWaypoint(waypoints, currentWaypointIndex) != currentBypassBuilding
                || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                return false;
            }

            if ((EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out uint dwellSinceFrame) || nowFrame <= dwellSinceFrame)
                return false;

            float elapsedFrames = nowFrame - dwellSinceFrame;
            int maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line);
            float timeoutRemainingFrames = 0f;
            if (maxStationDwellMinutes > 0)
            {
                uint timeoutDeadlineFrame = ComputeAdjustedStopDwellDeadlineFrame(line, currentWaypointIndex, dwellSinceFrame, maxStationDwellMinutes);
                if (nowFrame < timeoutDeadlineFrame)
                    timeoutRemainingFrames = timeoutDeadlineFrame - nowFrame;
            }

            float estimatedRemainingFrames = 0f;
            if (TryGetObservedWaypointStopFrames(line, currentWaypointIndex, out float observedDwellFrames)
                && observedDwellFrames > 0f)
            {
                estimatedRemainingFrames = math.max(0f, observedDwellFrames - elapsedFrames);
            }

            if (!(estimatedRemainingFrames > 0f) && timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = timeoutRemainingFrames;

            if (timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = math.min(estimatedRemainingFrames, timeoutRemainingFrames);

            if (!(estimatedRemainingFrames > 0f))
                return false;

            remainingFrames = estimatedRemainingFrames;
            return true;
        }

        private void BeginObservedStopDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || waypointIndex < 0)
                return;

            m_StopDwellSessions[vehicle] = new StopDwellSession(line, waypointIndex, nowFrame);
        }

        private void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return;
            if (!m_StopDwellSessions.TryGetValue(vehicle, out StopDwellSession session))
                return;

            m_StopDwellSessions.Remove(vehicle);

            int waypointIndex = session.WaypointIndex >= 0 ? session.WaypointIndex : fallbackWaypointIndex;
            if (waypointIndex < 0 || session.Line != line || nowFrame <= session.StartFrame)
                return;

            uint sampleFrames = nowFrame - session.StartFrame;
            if (sampleFrames == 0)
                return;

            const float maxObservedMinutes = 30f;
            float sampleMinutes = sampleFrames / (float)SIM_FRAMES_PER_MINUTE;
            if (sampleMinutes <= 0f || sampleMinutes > maxObservedMinutes)
                return;

            ulong key = MakeLineWaypointStopObservationKey(line, waypointIndex);
            if (m_WaypointStopDwellObservations.TryGetValue(key, out StopDwellObservation existing))
            {
                int sampleCount = math.min(existing.SampleCount + 1, 8);
                float averageFrames = existing.SampleCount <= 0
                    ? sampleFrames
                    : ((existing.AverageFrames * existing.SampleCount) + sampleFrames) / (existing.SampleCount + 1);
                m_WaypointStopDwellObservations[key] = new StopDwellObservation
                {
                    AverageFrames = averageFrames,
                    SampleCount = sampleCount
                };
                FlushStopDwellObservation(line, waypointIndex, m_WaypointStopDwellObservations[key]);
                InvalidateTrackTimingForLine(line);
                return;
            }

            m_WaypointStopDwellObservations[key] = new StopDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1
            };
            FlushStopDwellObservation(line, waypointIndex, m_WaypointStopDwellObservations[key]);
            InvalidateTrackTimingForLine(line);
        }

        private bool ShouldForceMidStopDwellTimeout(
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
            maxDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line);
            if (!boarding || currentWaypointIndex <= 0 || currentWaypointIndex >= waypointCount)
            {
                if (m_StopDwellStartFrame.Remove(vehicle))
                {
                    ClearForcedMidStopClosingConsist(vehicle);
                    log.Info("[StopDwellEnd] line" + line.Index
                        + " vehicle" + vehicle.Index
                        + " boarding=" + boarding
                        + " wp=" + currentWaypointIndex
                        + "/" + (waypointCount - 1)
                        + " nowFrame=" + nowFrame);
                }
                return false;
            }

            if (maxDwellMinutes <= 0)
                return false;

            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out dwellSinceFrame))
            {
                dwellSinceFrame = nowFrame;
                m_StopDwellStartFrame[vehicle] = dwellSinceFrame;
                dwellDeadlineFrame = ComputeAdjustedStopDwellDeadlineFrame(line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
                log.Info("[StopDwellBegin] line" + line.Index
                    + " vehicle" + vehicle.Index
                    + " wp=" + currentWaypointIndex
                    + "/" + (waypointCount - 1)
                    + " limit=" + maxDwellMinutes + "min"
                    + " deadlineFrame=" + dwellDeadlineFrame);
                return false;
            }

            dwellDeadlineFrame = ComputeAdjustedStopDwellDeadlineFrame(line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
            return nowFrame >= dwellDeadlineFrame;
        }

        private uint ComputeAdjustedStopDwellDeadlineFrame(
            Entity line,
            int waypointIndex,
            uint dwellSinceFrame,
            int maxDwellMinutes)
        {
            float configuredFrames = math.max(0f, maxDwellMinutes * (float)SIM_FRAMES_PER_MINUTE);
            float earlyCloseFrames = 0f;

            if (line != Entity.Null
                && waypointIndex >= 0
                && m_WaypointStopDwellObservations.TryGetValue(
                    MakeLineWaypointStopObservationKey(line, waypointIndex),
                    out StopDwellObservation observation)
                && observation.AverageFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observation.AverageFrames - configuredFrames,
                    EARLY_STOP_DWELL_CLOSE_MAX_MINUTES * (float)SIM_FRAMES_PER_MINUTE);
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
        }

        private void TryRecordPreparingArrivalSample(Entity v, Entity line, uint nowFrame)
        {
            if (line == Entity.Null)
            {
                m_VehiclePreparingStartFrame.Remove(v);
                m_VehicleDispatchRequestStartFrame.Remove(v);
                return;
            }

            uint frames = 0;
            bool hasSample = false;
            if (m_VehicleDispatchRequestStartFrame.TryGetValue(v, out uint dispatchRequestStart))
            {
                frames = nowFrame - dispatchRequestStart;
                hasSample = true;
            }
            else if (m_VehiclePreparingStartFrame.TryGetValue(v, out uint prepStart))
            {
                frames = nowFrame - prepStart;
                hasSample = true;
            }

            m_VehiclePreparingStartFrame.Remove(v);
            m_VehicleDispatchRequestStartFrame.Remove(v);
            if (!hasSample)
                return;
            if (frames == 0)
                return;

            float sampleMinutes = frames / (float)SIM_FRAMES_PER_MINUTE;
            if (sampleMinutes < DISPATCH_ESTIMATE_MIN_MINUTES || sampleMinutes > DISPATCH_ESTIMATE_MAX_MINUTES)
            {
                log.Info("[DispatchSample] line" + line.Index + " vehicle" + v.Index
                    + " sample=" + sampleMinutes.ToString("F1") + "min out-of-range skip");
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            RecordLineDispatchSampleSummary(line, nowMin, v, sampleMinutes);
            UpdateDispatchCache(line, frames);
        }
    }
}
