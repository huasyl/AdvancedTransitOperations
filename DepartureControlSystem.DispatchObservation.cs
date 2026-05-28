using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
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
            m_LapObservations.Start(v, currentOdo, nowFrame);
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
            if (!m_LapObservations.TryStart(v, out float startOdo))
                return;

            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            float lapDist = current - startOdo;
            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "line" + le.Index : "line?";

            if (m_LapObservations.ConsumeRestored(v))
            {
                if (lapDist > 0f)
                    m_LapObservations.SetDistance(v, lapDist);
                ClearVehicleTraversalSliceLapDebug(v);
                log.Info("[LapStatsSkipRestored] " + lineTag + " vehicle" + v.Index
                    + " lapDist=" + (lapDist / 1000f).ToString("F2") + "km"
                    + " restored-first-lap skip-lap-time-write");
                return;
            }

            if (lapDist > 0f)
            {
                m_LapObservations.SetDistance(v, lapDist);
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

            if (m_LapObservations.TryStartFrame(v, out uint startFrame))
            {
                uint framesDelta = m_SimulationSystem.frameIndex - startFrame;
                m_LapObservations.SetFrames(v, framesDelta);
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
            if (!ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, nowFrame))
                return;

            if (vehicle != Entity.Null
                && line != Entity.Null
                && m_TraversalSlices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession existingSession)
                && existingSession.Line == line
                && existingSession.SliceIndex >= 0
                && TryGetLineTrackChain(line, waypoints, out LineTrackChain existingChain)
                && existingChain?.TraversalProfile != null
                && existingSession.SliceIndex < existingChain.TraversalProfile.RunSlices.Count
                && TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, existingChain, out VehicleTrackCursor existingCursor))
            {
                TraversalRunSlice existingSlice = existingChain.TraversalProfile.RunSlices[existingSession.SliceIndex];
                int existingAtomIndex = math.clamp(existingCursor.AtomCursorIndex, 0, existingChain.TrackAtoms.Count - 1);
                if (existingAtomIndex >= existingSlice.StartAtomIndex
                    && existingAtomIndex < existingSlice.EndAtomIndexExclusive)
                {
                    MaybeRecordTraversalPositionSample(vehicle, line, existingChain, existingSession.SliceIndex, existingCursor, nowFrame);
                    m_TraversalSlices.LastSampleFrames[vehicle] = nowFrame;
                    return;
                }
            }

            if (!TryGetCurrentTraversalRunSlice(
                    vehicle,
                    line,
                    waypoints,
                    out LineTrackChain chain,
                    out int sliceIndex,
                    out VehicleTrackCursor cursor))
            {
                if (m_TraversalSlices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession droppedSession))
                    RecordTraversalSliceLapDebugDropped(vehicle, droppedSession.SliceIndex);
                m_TraversalSlices.Sessions.Remove(vehicle);
                m_TraversalSlices.Plans.Remove(vehicle);
                return;
            }

            if (m_TraversalSlices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                && session.Line == line
                && session.SliceIndex == sliceIndex)
            {
                m_TraversalSlices.LastSampleFrames[vehicle] = nowFrame;
                return;
            }

            FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame, cursor.AtomCursorIndex, cursor.AtomPosition01);
            if (chain != null
                && chain.TraversalProfile != null
                && sliceIndex >= 0
                && sliceIndex < chain.TraversalProfile.RunSlices.Count)
            {
                RecordTraversalSliceLapDebugStart(vehicle, chain.TraversalProfile.RunSlices[sliceIndex], cursor.AtomCursorIndex, cursor.AtomPosition01);
            }

            m_TraversalSlices.Plans.Remove(vehicle);
            m_TraversalSlices.Sessions[vehicle] = new VehicleTraversalSliceSession(line, sliceIndex, nowFrame, cursor.AtomCursorIndex, cursor.AtomPosition01);
            MaybeRecordTraversalPositionSample(vehicle, line, chain, sliceIndex, cursor, nowFrame);
            m_TraversalSlices.LastSampleFrames[vehicle] = nowFrame;
        }

        private bool ShouldSampleVehicleTraversalSliceObservation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            if (!TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, out TraversalSliceSamplingPlan plan))
                return true;

            if (plan.IsHighSampling)
                return true;

            if (!m_TraversalSlices.LastSampleFrames.TryGetValue(vehicle, out uint lastSampleFrame))
                return true;

            return nowFrame <= lastSampleFrame
                || (nowFrame - lastSampleFrame) >= plan.SampleIntervalFrames;
        }

        private bool TryBuildTraversalSliceSamplingPlan(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out TraversalSliceSamplingPlan plan)
        {
            plan = default;
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !m_TraversalSlices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line != line)
            {
                m_TraversalSlices.Plans.Remove(vehicle);
                return false;
            }

            if (!m_TrackModelQuery.TryProfile(line, out LineTraversalProfile profile)
                || !m_TrackModelQuery.TryChain(line, out LineTrackChain chain)
                || profile.SegmentSliceCutPointProgresses == null)
            {
                m_TraversalSlices.Plans.Remove(vehicle);
                return false;
            }

            if (m_TraversalSlices.Plans.TryGetValue(vehicle, out TraversalSliceSamplingPlanCache cachedPlan)
                && cachedPlan.Line == line
                && cachedPlan.ChainSignature == chain.Signature
                && cachedPlan.SliceIndex == session.SliceIndex
                && nowFrame < cachedPlan.NextRefreshFrame)
            {
                plan = cachedPlan.Plan;
                return true;
            }

            if (!TryBuildTraversalSliceSamplingPlanUncached(vehicle, waypoints, chain, out plan))
            {
                m_TraversalSlices.Plans.Remove(vehicle);
                return false;
            }

            uint refreshFrames = math.max(1u, plan.SampleIntervalFrames);
            uint nextRefreshFrame = nowFrame + refreshFrames;
            m_TraversalSlices.Plans[vehicle] = new TraversalSliceSamplingPlanCache(
                line,
                chain.Signature,
                session.SliceIndex,
                nextRefreshFrame,
                plan);
            return true;
        }

        private bool TryBuildTraversalSliceSamplingPlanUncached(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out TraversalSliceSamplingPlan plan)
        {
            plan = default;
            if (!TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return false;

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int segmentIndex = nextWaypointIndex == 0
                ? math.max(0, waypoints.Length - 1)
                : nextWaypointIndex - 1;
            if (segmentIndex < 0
                || segmentIndex >= chain.TraversalProfile.SegmentSliceCutPointProgresses.Length)
            {
                return false;
            }

            uint sampleIntervalFrames = TRAVERSAL_SLICE_SAMPLE_INTERVAL_LOW_FRAMES;
            float[] cutPoints = chain.TraversalProfile.SegmentSliceCutPointProgresses[segmentIndex];
            bool isHighSampling = false;
            bool isMediumSampling = false;
            bool hasUpcomingCutPoint = false;
            float upcomingCutPointProgress = 0f;
            float upcomingCutPointDistance = float.MaxValue;
            if (cutPoints != null && cutPoints.Length > 0)
            {
                float nearestCutPointDistance = float.MaxValue;
                float saturatedSegmentPosition = math.saturate(segmentPosition);
                for (int cutPointIndex = 0; cutPointIndex < cutPoints.Length; cutPointIndex++)
                {
                    float distance = math.abs(saturatedSegmentPosition - cutPoints[cutPointIndex]);
                    if (distance < nearestCutPointDistance)
                        nearestCutPointDistance = distance;
                    if (cutPoints[cutPointIndex] >= saturatedSegmentPosition)
                    {
                        float forwardDistance = cutPoints[cutPointIndex] - saturatedSegmentPosition;
                        if (!hasUpcomingCutPoint || forwardDistance < upcomingCutPointDistance)
                        {
                            hasUpcomingCutPoint = true;
                            upcomingCutPointDistance = forwardDistance;
                            upcomingCutPointProgress = cutPoints[cutPointIndex];
                        }
                    }
                }

                if (nearestCutPointDistance <= TRAVERSAL_SLICE_SAMPLE_HIGH_THRESHOLD)
                {
                    isHighSampling = true;
                    sampleIntervalFrames = 1;
                }
                else if (nearestCutPointDistance <= TRAVERSAL_SLICE_SAMPLE_MEDIUM_THRESHOLD)
                {
                    isMediumSampling = true;
                    sampleIntervalFrames = TRAVERSAL_SLICE_SAMPLE_INTERVAL_MEDIUM_FRAMES;
                }
            }

            plan = new TraversalSliceSamplingPlan(
                true,
                segmentIndex,
                math.saturate(segmentPosition),
                sampleIntervalFrames,
                isHighSampling,
                isMediumSampling,
                hasUpcomingCutPoint,
                upcomingCutPointProgress,
                hasUpcomingCutPoint ? upcomingCutPointDistance : float.MaxValue);
            return true;
        }

        private void FinalizeVehicleTraversalSliceObservation(
            Entity vehicle,
            uint nowFrame,
            int exitAtomIndex = -1,
            float exitAtomPosition01 = 0f)
        {
            if (vehicle == Entity.Null
                || !m_TraversalSlices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line == Entity.Null
                || session.SliceIndex < 0
                || nowFrame <= session.EnterFrame)
            {
                m_TraversalSlices.Sessions.Remove(vehicle);
                m_TraversalSlices.Plans.Remove(vehicle);
                return;
            }

            float observedFrames = nowFrame - session.EnterFrame;
            m_TraversalSlices.RecordActualSample(new TraversalSliceActualSample(
                session.Line,
                vehicle,
                session.SliceIndex,
                session.EnterFrame,
                nowFrame,
                session.EnterAtomIndex,
                session.EnterAtomPosition01,
                exitAtomIndex,
                exitAtomPosition01));
            RecordTraversalSliceLapDebugFinalize(vehicle, session.SliceIndex, observedFrames);
            ulong key = MakeTraversalSliceObservationKey(session.Line, session.SliceIndex);
            if (m_TraversalSlices.TryObservation(key, out TraversalSliceObservation existing))
            {
                int sampleCount = existing.SampleCount + 1;
                float averageFrames = ((existing.AverageFrames * existing.SampleCount) + observedFrames) / sampleCount;
                float fastBaselineFrames = ComputeFastTraversalBaselineFrames(existing.FastBaselineFrames, observedFrames);
                TraversalSliceObservation updated = new TraversalSliceObservation(averageFrames, fastBaselineFrames, sampleCount, nowFrame);
                m_TraversalSlices.Record(key, updated);
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, updated);
            }
            else
            {
                TraversalSliceObservation created = new TraversalSliceObservation(observedFrames, observedFrames, 1, nowFrame);
                m_TraversalSlices.Record(key, created);
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, created);
            }

            m_TraversalSlices.Sessions.Remove(vehicle);
            m_TraversalSlices.Plans.Remove(vehicle);
        }

        private void MaybeRecordTraversalPositionSample(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int sliceIndex,
            VehicleTrackCursor cursor,
            uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return;

            uint sampleIntervalFrames = (uint)math.max(1f, math.round((float)SIM_FRAMES_PER_MINUTE));
            if (m_TraversalSlices.LastPositionSampleFrames.TryGetValue(vehicle, out uint lastFrame)
                && nowFrame > lastFrame
                && nowFrame - lastFrame < sampleIntervalFrames)
            {
                return;
            }

            m_TraversalSlices.LastPositionSampleFrames[vehicle] = nowFrame;
            Entity physicalLane = Entity.Null;
            int atomIndex = math.clamp(cursor.AtomCursorIndex, 0, math.max(0, chain.TrackAtoms.Count - 1));
            if (atomIndex >= 0 && atomIndex < chain.TrackAtoms.Count)
                physicalLane = chain.TrackAtoms[atomIndex].Key.PhysicalLaneKey;

            int segmentIndex = cursor.SegmentIndex;
            float segmentPosition = -1f;
            if (TryGetRouteProgress(vehicle, out int routeNextWaypointIndex, out float routeSegmentPosition))
            {
                segmentIndex = routeNextWaypointIndex == 0
                    ? math.max(0, chain.SegmentRanges.Count - 1)
                    : routeNextWaypointIndex - 1;
                segmentPosition = math.saturate(routeSegmentPosition);
            }

            float speedMetersPerSecond = 0f;
            if (EntityManager.HasComponent<Moving>(vehicle))
                speedMetersPerSecond = math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity);

            float odometerMeters = -1f;
            if (EntityManager.HasComponent<Odometer>(vehicle))
                odometerMeters = EntityManager.GetComponentData<Odometer>(vehicle).m_Distance;

            m_TraversalSlices.RecordPositionSample(new TraversalPositionSample(
                line,
                vehicle,
                nowFrame,
                sliceIndex,
                segmentIndex,
                segmentPosition,
                atomIndex,
                math.saturate(cursor.AtomPosition01),
                physicalLane,
                speedMetersPerSecond,
                odometerMeters));
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
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            chain = null;
            sliceIndex = -1;
            cursor = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out chain)
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
            {
                return false;
            }

            int atomIndex = math.clamp(cursor.AtomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            if (chain.TraversalProfile.AtomToRunSliceIndex == null
                || atomIndex < 0
                || atomIndex >= chain.TraversalProfile.AtomToRunSliceIndex.Length)
            {
                return false;
            }

            sliceIndex = chain.TraversalProfile.AtomToRunSliceIndex[atomIndex];
            return sliceIndex >= 0
                && sliceIndex < chain.TraversalProfile.RunSlices.Count;
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
            if (m_TraversalSlices.TryObservation(key, out TraversalSliceObservation observation)
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
            if (!m_TraversalSlices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordStart(enterOffsetAtoms, midSliceStart);
            m_TraversalSlices.LapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugDropped(Entity vehicle, int sliceIndex)
        {
            if (vehicle == Entity.Null || sliceIndex < 0)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_TraversalSlices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.DroppedWithoutFinalizeCount++;
            m_TraversalSlices.LapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugFinalize(Entity vehicle, int sliceIndex, float observedFrames)
        {
            if (vehicle == Entity.Null || sliceIndex < 0 || observedFrames <= 0f)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_TraversalSlices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordFinalize(observedFrames);
            m_TraversalSlices.LapDebug[key] = aggregate;
        }

        private void ClearVehicleTraversalSliceLapDebug(Entity vehicle)
        {
            if (vehicle == Entity.Null || m_TraversalSlices.LapDebug.Count == 0)
                return;

            List<ulong> removeKeys = null;
            foreach (var kvp in m_TraversalSlices.LapDebug)
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
                m_TraversalSlices.LapDebug.Remove(removeKeys[i]);
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
                bool hasObservation = m_TraversalSlices.TryObservation(observationKey, out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;
                ulong lapDebugKey = MakeVehicleTraversalSliceLapDebugKey(vehicle, slice.SliceIndex);
                bool hasLapDebug = m_TraversalSlices.LapDebug.TryGetValue(lapDebugKey, out TraversalSliceLapDebugAggregate lapDebug);
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

        private readonly struct StationStopDwellAnchor
        {
            public readonly string StationAnchorId;
            public readonly Entity AnchorEntity;
            public readonly Entity StopEntity;
            public readonly Entity BuildingEntity;

            public StationStopDwellAnchor(string stationAnchorId, Entity anchorEntity, Entity stopEntity, Entity buildingEntity)
            {
                StationAnchorId = stationAnchorId ?? string.Empty;
                AnchorEntity = anchorEntity;
                StopEntity = stopEntity;
                BuildingEntity = buildingEntity;
            }
        }

        private bool TryResolveStationStopDwellAnchor(Entity line, int waypointIndex, out StationStopDwellAnchor anchor)
        {
            anchor = default;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || waypointIndex < 0
                || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length)
                return false;

            Entity waypoint = waypoints[waypointIndex].m_Waypoint;
            Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
            if (stopEntity == Entity.Null)
                return false;

            Entity anchorEntity = ResolveStationAnchor(waypoint);
            if (anchorEntity == Entity.Null)
                anchorEntity = ResolveStationAnchorFromStop(stopEntity);
            if (anchorEntity == Entity.Null)
                return false;

            string stationAnchorId = EnsureStationAnchorKey(anchorEntity);
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !IsStationAnchorKeyId(stationAnchorId))
                return false;

            Entity buildingEntity = FindTransportStationFromStop(stopEntity);
            if (buildingEntity == Entity.Null)
                buildingEntity = ResolvePassingStationBuilding(stopEntity);

            anchor = new StationStopDwellAnchor(stationAnchorId, anchorEntity, stopEntity, buildingEntity);
            return true;
        }

        private string MakeStationStopDwellObservationKey(Entity line, string stationAnchorId)
        {
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !IsStationAnchorKeyId(stationAnchorId))
                return string.Empty;

            string lineId = GetWorkbenchLineId(line);
            if (string.IsNullOrWhiteSpace(lineId))
                lineId = line != Entity.Null ? "entity:" + line.Index.ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            return lineId + "|" + stationAnchorId;
        }

        private static bool IsStationStopDwellObservationKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int separatorIndex = value.IndexOf('|');
            return separatorIndex > 0
                && separatorIndex + 1 < value.Length
                && IsStationAnchorKeyId(value.Substring(separatorIndex + 1));
        }

        private bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
        {
            dwellFrames = 0f;
            if (line == Entity.Null || waypointIndex < 0)
                return false;

            if (TryResolveStationStopDwellAnchor(line, waypointIndex, out StationStopDwellAnchor anchor))
            {
                string observationKey = MakeStationStopDwellObservationKey(line, anchor.StationAnchorId);
                if (!string.IsNullOrWhiteSpace(observationKey)
                    && m_StopDwell.TryStation(observationKey, out StationStopDwellObservation anchorObservation)
                    && anchorObservation.AverageFrames > 0f
                    && anchorObservation.SampleCount > 0)
                {
                    dwellFrames = anchorObservation.AverageFrames;
                    return dwellFrames > 0f;
                }
            }

            return false;
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

            if (!m_StopDwell.TryStart(vehicle, out uint dwellSinceFrame) || nowFrame <= dwellSinceFrame)
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

            m_StopDwell.Begin(vehicle, line, waypointIndex, nowFrame);
        }

        private void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return;
            if (!m_StopDwell.End(vehicle, out StopDwellSession session))
                return;

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

            RecordStationStopDwellObservation(line, waypointIndex, sampleFrames, nowFrame, sampleMinutes);
            InvalidateTrackTimingForLine(line);
        }

        private void RecordStationStopDwellObservation(
            Entity line,
            int waypointIndex,
            float sampleFrames,
            uint nowFrame,
            float sampleMinutes)
        {
            m_StationAnchorDiagAcceptedSamples++;
            bool suspiciousOriginOrTerminal = IsSuspiciousOriginOrTerminalStationStopDwellSample(line, waypointIndex);
            if (suspiciousOriginOrTerminal)
            {
                m_StationAnchorDiagSuspiciousOriginOrTerminal++;
                m_StationAnchorDiagTotalSuspiciousOriginOrTerminal++;
            }
            if (sampleMinutes > EARLY_STOP_DWELL_CLOSE_MAX_MINUTES)
            {
                m_StationAnchorDiagSuspiciousLongDwell++;
                m_StationAnchorDiagTotalSuspiciousLongDwell++;
            }

            if (!TryResolveStationStopDwellAnchor(line, waypointIndex, out StationStopDwellAnchor anchor))
            {
                m_StationAnchorDiagAnchorMissing++;
                m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            if (suspiciousOriginOrTerminal)
            {
                m_StationAnchorDiagAnchorRejectedOriginOrTerminal++;
                m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            string observationKey = MakeStationStopDwellObservationKey(line, anchor.StationAnchorId);
            if (string.IsNullOrWhiteSpace(observationKey))
            {
                m_StationAnchorDiagAnchorMissing++;
                m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            if (m_StopDwell.TryStation(observationKey, out StationStopDwellObservation existing))
            {
                int sampleCount = math.min(existing.SampleCount + 1, 32);
                float averageFrames = existing.SampleCount <= 0
                    ? sampleFrames
                    : ((existing.AverageFrames * existing.SampleCount) + sampleFrames) / (existing.SampleCount + 1);
                StationStopDwellObservation updated = new StationStopDwellObservation
                {
                    AverageFrames = averageFrames,
                    SampleCount = sampleCount,
                    LastObservedFrame = nowFrame
                };
                m_StopDwell.RecordStation(observationKey, updated);
                FlushStationStopDwellObservation(observationKey, updated);
                m_StationAnchorDiagAnchorWritten++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            StationStopDwellObservation created = new StationStopDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1,
                LastObservedFrame = nowFrame
            };
            m_StopDwell.RecordStation(observationKey, created);
            FlushStationStopDwellObservation(observationKey, created);
            m_StationAnchorDiagAnchorWritten++;
            MaybeLogStationAnchorObservationDiagnostics(nowFrame);
        }

        private bool IsSuspiciousOriginOrTerminalStationStopDwellSample(Entity line, int waypointIndex)
        {
            if (line == Entity.Null || waypointIndex < 0 || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            return waypointIndex == 0;
        }

        private void MaybeLogStationAnchorObservationDiagnostics(uint nowFrame)
        {
            if (m_StationAnchorObservationDiagLastLogFrame != 0
                && nowFrame - m_StationAnchorObservationDiagLastLogFrame < STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES)
            {
                return;
            }

            if (m_StationAnchorDiagAcceptedSamples == 0
                && m_StationAnchorDiagLegacyWritten == 0
                && m_StationAnchorDiagAnchorWritten == 0
                && m_StationAnchorDiagAnchorMissing == 0)
            {
                m_StationAnchorObservationDiagLastLogFrame = nowFrame;
                return;
            }

            StationAnchorObservationSummaryDto coverage = BuildStationAnchorObservationDiagnostics().summary;
            log.Info("[StationAnchorDiag] intervalFrames=" + STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " lines=" + coverage.lineCount
                + " stopWaypoints=" + coverage.stopWaypointCount
                + " anchorResolved=" + coverage.anchorResolvedCount
                + " anchorMissing=" + coverage.anchorMissingCount
                + " uniqueAnchors=" + coverage.uniqueAnchorCount
                + " duplicateAnchorOccurrences=" + coverage.duplicateAnchorOccurrenceCount);

            log.Info("[StopDwellAnchorDiag] intervalFrames=" + STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " accepted=" + m_StationAnchorDiagAcceptedSamples
                + " legacyWritten=" + m_StationAnchorDiagLegacyWritten
                + " anchorWritten=" + m_StationAnchorDiagAnchorWritten
                + " anchorMissing=" + m_StationAnchorDiagAnchorMissing
                + " anchorRejectedOriginOrTerminal=" + m_StationAnchorDiagAnchorRejectedOriginOrTerminal
                + " uniqueAnchors=" + m_StopDwell.Stations.Count
                + " suspiciousOriginOrTerminal=" + m_StationAnchorDiagSuspiciousOriginOrTerminal
                + " suspiciousLongDwell=" + m_StationAnchorDiagSuspiciousLongDwell);

            m_StationAnchorObservationDiagLastLogFrame = nowFrame;
            m_StationAnchorDiagAcceptedSamples = 0;
            m_StationAnchorDiagLegacyWritten = 0;
            m_StationAnchorDiagAnchorWritten = 0;
            m_StationAnchorDiagAnchorMissing = 0;
            m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_StationAnchorDiagSuspiciousLongDwell = 0;
        }

        private void ClearStationAnchorObservationDiagnosticsState()
        {
            m_LastStationStopDwellLegacyBufferCount = 0;
            m_LastStationStopDwellLegacyRestoredCount = 0;
            m_LastStationStopDwellAnchorBufferCount = 0;
            m_LastStationStopDwellAnchorRestoredCount = 0;
            m_StationAnchorObservationDiagLastLogFrame = 0;
            m_StationAnchorDiagAcceptedSamples = 0;
            m_StationAnchorDiagLegacyWritten = 0;
            m_StationAnchorDiagAnchorWritten = 0;
            m_StationAnchorDiagAnchorMissing = 0;
            m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_StationAnchorDiagSuspiciousLongDwell = 0;
            m_StationAnchorDiagTotalAnchorMissing = 0;
            m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal = 0;
            m_StationAnchorDiagTotalSuspiciousOriginOrTerminal = 0;
            m_StationAnchorDiagTotalSuspiciousLongDwell = 0;
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
                if (m_StopDwell.RemoveStart(vehicle))
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

            if (!m_StopDwell.TryStart(vehicle, out dwellSinceFrame))
            {
                dwellSinceFrame = nowFrame;
                m_StopDwell.SetStart(vehicle, dwellSinceFrame);
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
                && TryGetObservedWaypointStopFrames(line, waypointIndex, out float observationFrames)
                && observationFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observationFrames - configuredFrames,
                    EARLY_STOP_DWELL_CLOSE_MAX_MINUTES * (float)SIM_FRAMES_PER_MINUTE);
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
        }

        private bool TryCaptureTrainHeadSnapshot(Entity vehicle, int waypointIndex, out TrainHeadSnapshot snapshot)
        {
            snapshot = default;
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return false;

            Entity headVehicle = vehicle;
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length > 0 && layout[0].m_Vehicle != Entity.Null)
                    headVehicle = layout[0].m_Vehicle;
            }

            if (headVehicle == Entity.Null
                || !EntityManager.Exists(headVehicle)
                || !EntityManager.HasComponent<TrainCurrentLane>(headVehicle))
            {
                return false;
            }

            TrainCurrentLane currentLane = EntityManager.GetComponentData<TrainCurrentLane>(headVehicle);
            bool reversed = EntityManager.HasComponent<Train>(headVehicle)
                && (EntityManager.GetComponentData<Train>(headVehicle).m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0;

            snapshot = new TrainHeadSnapshot(
                m_SimulationSystem.frameIndex,
                headVehicle,
                currentLane.m_Front.m_Lane,
                currentLane.m_Rear.m_Lane,
                reversed,
                waypointIndex);
            return true;
        }

        private static string FormatTrainHeadSnapshotEntity(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
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
            UpdateDispatchCache(line, v, frames);
        }
    }
}
