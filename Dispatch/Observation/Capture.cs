using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct ObservedDwellSample
    {
        internal readonly Entity Line;
        internal readonly int WaypointIndex;
        internal readonly float SampleFrames;
        internal readonly uint Frame;
        internal readonly float SampleMinutes;

        internal ObservedDwellSample(Entity line, int waypointIndex, float sampleFrames, uint frame, float sampleMinutes)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            SampleFrames = sampleFrames;
            Frame = frame;
            SampleMinutes = sampleMinutes;
        }
    }

    internal readonly struct StationDwellAnchor
    {
        internal readonly string StationAnchorId;
        internal readonly Entity AnchorEntity;
        internal readonly Entity StopEntity;
        internal readonly Entity BuildingEntity;

        internal StationDwellAnchor(string stationAnchorId, Entity anchorEntity, Entity stopEntity, Entity buildingEntity)
        {
            StationAnchorId = stationAnchorId ?? string.Empty;
            AnchorEntity = anchorEntity;
            StopEntity = stopEntity;
            BuildingEntity = buildingEntity;
        }
    }

    internal sealed class Capture
    {
        private const uint TraversalSliceSampleIntervalHighFrames = 16;
        private const uint TraversalSliceSampleIntervalMediumFrames = 32;
        private const uint TraversalSliceSampleIntervalLowFrames = 64;
        private const uint TraversalSliceLineEligibilityNegativeCacheFrames = 60;
        private const uint TraversalSliceEntryProbeIntervalFrames = 32;
        private const float TraversalSliceSampleHighThreshold = 0.03f;
        private const float TraversalSliceSampleMediumThreshold = 0.05f;
        private const float MaxObservedDwellMinutes = 30f;

        private readonly LapStore m_Laps;
        private readonly DwellStore m_Dwell;
        private readonly SliceStore m_Slices;
        private readonly SliceAdmission m_Admission;
        private readonly TrackModelService m_TrackModel;
        private readonly TrackProjectionService m_TrackProjection;
        private readonly CapturePort m_Port;

        internal Capture(
            LapStore laps,
            DwellStore dwell,
            SliceStore slices,
            SliceAdmission admission,
            TrackModelService trackModel,
            TrackProjectionService trackProjection,
            CapturePort port)
        {
            m_Laps = laps;
            m_Dwell = dwell;
            m_Slices = slices;
            m_Admission = admission;
            m_TrackModel = trackModel;
            m_TrackProjection = trackProjection;
            m_Port = port;
        }

        internal void RecordLapStart(Entity vehicle, string reason = "")
        {
            Entity line = m_Port.LineOf(vehicle);
            if (!m_Port.HasOdo(vehicle))
            {
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                    m_Port.Log("[LapStartSkip] " + lineTag + " vehicle" + vehicle.Index
                        + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                        + " no-odometer");
                }
                return;
            }

            float currentOdo = m_Port.Odo(vehicle);
            uint nowFrame = m_Port.Frame();
            m_Laps.Start(vehicle, currentOdo, nowFrame);
            if (RtLog.VerboseEnabled)
            {
                string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                int slot = m_Port.SlotOf(vehicle);
                string curSlot = slot >= 0 ? SlotStr(slot) : "-";
                int cachedWp = m_Port.CachedWp(vehicle);
                m_Port.Log("[LapStart] " + lineTag + " vehicle" + vehicle.Index
                    + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                    + " frame=" + nowFrame
                    + " odo=" + currentOdo.ToString("F1")
                    + " curSlot=" + curSlot
                    + " cachedWp=" + cachedWp);
            }
        }

        internal void UpdateLapStats(Entity vehicle)
        {
            if (!m_Port.HasOdo(vehicle))
                return;
            if (!m_Laps.TryStart(vehicle, out float startOdo))
                return;

            float current = m_Port.Odo(vehicle);
            float lapDist = current - startOdo;
            Entity line = m_Port.LineOf(vehicle);

            if (m_Laps.ConsumeRestored(vehicle))
            {
                if (lapDist > 0f)
                    m_Laps.SetDistance(vehicle, lapDist);
                ClearVehicleTraversalSliceLapDebug(vehicle);
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                    m_Port.Log("[LapStatsSkipRestored] " + lineTag + " vehicle" + vehicle.Index
                        + " lapDist=" + (lapDist / 1000f).ToString("F2") + "km"
                        + " restored-first-lap skip-lap-time-write");
                }
                return;
            }

            if (lapDist > 0f)
            {
                m_Laps.SetDistance(vehicle, lapDist);
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                    float maintenanceRange = m_Port.Range(vehicle);
                    float remaining = maintenanceRange > 0f ? maintenanceRange - current : -1f;
                    string maintStr = maintenanceRange > 0f
                        ? " maintenance=" + (maintenanceRange / 1000f).ToString("F1") + "km remaining=" + (remaining / 1000f).ToString("F1") + "km"
                        : " maintenance=none";
                    m_Port.Log("[LapDistance] " + lineTag + " vehicle" + vehicle.Index
                        + " lap=" + (lapDist / 1000f).ToString("F2") + "km" + maintStr);
                }
            }

            if (!m_Laps.TryStartFrame(vehicle, out uint startFrame))
                return;

            uint framesDelta = m_Port.Frame() - startFrame;
            m_Laps.SetFrames(vehicle, framesDelta);
            float realMinutes = (float)m_Port.ToMinutes(framesDelta);
            if (RtLog.VerboseEnabled)
            {
                string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                m_Port.Log("[LapStats] " + lineTag + " vehicle" + vehicle.Index
                    + " lap=" + realMinutes.ToString("F1") + "min/" + framesDelta + "frames");

                if (line != Entity.Null
                    && m_Port.Express(line)
                    && m_Port.HasWaypoints(line))
                {
                    DynamicBuffer<RouteWaypoint> timingWaypoints = m_Port.Waypoints(line);
                    if (TryGetTraversalProfileLapTiming(
                            line,
                            timingWaypoints,
                            out float profileRunFrames,
                            out float profileStopFrames,
                            out int profileStopCount,
                            out int profilePassCount))
                    {
                        float profileTotalFrames = profileRunFrames + profileStopFrames;
                        m_Port.Log("[ExpressLapProfile] " + lineTag + " vehicle" + vehicle.Index
                            + " observed=" + realMinutes.ToString("F1") + "min"
                            + " profileTotal=" + m_Port.ToMinutes(profileTotalFrames).ToString("F1") + "min"
                            + " run=" + m_Port.ToMinutes(profileRunFrames).ToString("F1") + "min"
                            + " stop=" + m_Port.ToMinutes(profileStopFrames).ToString("F1") + "min"
                            + " stopCount=" + profileStopCount
                            + " passCount=" + profilePassCount);
                        LogTraversalProfileLapSlices(vehicle, line, timingWaypoints);
                    }
                }
            }

            if (line != Entity.Null)
                m_Port.FlushLap(line);

            ClearVehicleTraversalSliceLapDebug(vehicle);
        }

        internal bool TryGetTraversalProfileLapTiming(
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
                || !m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            m_TrackModel.EnsureBypassPipelineReady(chain);
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

        internal void UpdateVehicleTraversalSliceObservation(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, uint nowFrame)
        {
            if (!m_Admission.CanObserve(vehicle))
                return;

            bool hasExistingSession = vehicle != Entity.Null
                && m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession entrySession)
                && entrySession.Line == line;
            if (!hasExistingSession
                && m_Slices.NextEntryProbeFrames.TryGetValue(vehicle, out uint nextEntryProbeFrame)
                && nowFrame < nextEntryProbeFrame)
            {
                return;
            }

            if (hasExistingSession
                && m_Slices.NextSampleFrames.TryGetValue(vehicle, out uint nextSampleFrame)
                && nowFrame < nextSampleFrame)
            {
                return;
            }

            if (!hasExistingSession)
            {
                m_Slices.NextSampleFrames.Remove(vehicle);
            }

            if (!TryGetEligibleTraversalSliceChain(line, waypoints, nowFrame, out LineTrackChain eligibleChain))
                return;

            if (!ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, eligibleChain, nowFrame, out TraversalSliceSamplingPlan samplingPlan))
            {
                return;
            }

            if (vehicle != Entity.Null
                && line != Entity.Null
                && m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession existingSession)
                && existingSession.Line == line
                && existingSession.SliceIndex >= 0
                && eligibleChain?.TraversalProfile != null
                && existingSession.SliceIndex < eligibleChain.TraversalProfile.RunSlices.Count
                && m_TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, eligibleChain, out VehicleTrackCursor existingCursor))
            {
                TraversalRunSlice existingSlice = eligibleChain.TraversalProfile.RunSlices[existingSession.SliceIndex];
                int existingAtomIndex = math.clamp(existingCursor.AtomCursorIndex, 0, eligibleChain.TrackAtoms.Count - 1);
                if (existingAtomIndex >= existingSlice.StartAtomIndex
                    && existingAtomIndex < existingSlice.EndAtomIndexExclusive)
                {
                    MaybeRecordTraversalPositionSample(vehicle, line, eligibleChain, existingSession.SliceIndex, existingCursor, nowFrame);
                    m_Slices.LastSampleFrames[vehicle] = nowFrame;
                    ScheduleNextTraversalSliceSample(vehicle, samplingPlan, nowFrame);
                    return;
                }
            }

            if (!TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, eligibleChain, out LineTrackChain chain, out int sliceIndex, out VehicleTrackCursor cursor))
            {
                if (m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession droppedSession))
                    RecordTraversalSliceLapDebugDropped(vehicle, droppedSession.SliceIndex);
                m_Slices.Sessions.Remove(vehicle);
                m_Slices.Plans.Remove(vehicle);
                m_Slices.NextSampleFrames.Remove(vehicle);
                ScheduleNextTraversalSliceEntryProbe(vehicle, nowFrame);
                return;
            }

            m_Slices.NextEntryProbeFrames.Remove(vehicle);

            if (m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                && session.Line == line
                && session.SliceIndex == sliceIndex)
            {
                m_Slices.LastSampleFrames[vehicle] = nowFrame;
                ScheduleNextTraversalSliceSample(vehicle, samplingPlan, nowFrame);
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

            m_Slices.Plans.Remove(vehicle);
            m_Slices.Sessions[vehicle] = new VehicleTraversalSliceSession(line, sliceIndex, nowFrame, cursor.AtomCursorIndex, cursor.AtomPosition01);
            MaybeRecordTraversalPositionSample(vehicle, line, chain, sliceIndex, cursor, nowFrame);
            m_Slices.LastSampleFrames[vehicle] = nowFrame;
            ScheduleNextTraversalSliceSample(vehicle, samplingPlan, nowFrame);
        }

        internal bool ShouldSampleVehicleTraversalSliceObservation(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, uint nowFrame)
        {
            return ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, null, nowFrame);
        }

        internal bool IsSourceSliceDue(Entity vehicle, Entity line, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !m_Admission.CanObserve(vehicle))
                return false;

            if (m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                && session.Line == line)
            {
                return !m_Slices.NextSampleFrames.TryGetValue(vehicle, out uint nextSampleFrame)
                    || nowFrame >= nextSampleFrame;
            }

            return !m_Slices.NextEntryProbeFrames.TryGetValue(vehicle, out uint nextEntryProbeFrame)
                || nowFrame >= nextEntryProbeFrame;
        }

        private bool ShouldSampleVehicleTraversalSliceObservation(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain knownChain, uint nowFrame)
        {
            return ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, knownChain, nowFrame, out _);
        }

        private bool ShouldSampleVehicleTraversalSliceObservation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain knownChain,
            uint nowFrame,
            out TraversalSliceSamplingPlan plan)
        {
            plan = default;
            if (!TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, knownChain, out plan))
            {
                if (vehicle != Entity.Null
                    && m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                    && session.Line == line)
                {
                    plan = new TraversalSliceSamplingPlan(
                        false,
                        -1,
                        0f,
                        TraversalSliceSampleIntervalLowFrames,
                        false,
                        false,
                        false,
                        0f,
                        float.MaxValue);
                    return true;
                }

                return true;
            }

            if (plan.IsHighSampling)
                return true;

            if (!m_Slices.LastSampleFrames.TryGetValue(vehicle, out uint lastSampleFrame))
                return true;

            uint nextSampleFrame = lastSampleFrame + math.max(1u, plan.SampleIntervalFrames);
            m_Slices.NextSampleFrames[vehicle] = nextSampleFrame;
            return nowFrame <= lastSampleFrame || nowFrame >= nextSampleFrame;
        }

        private void ScheduleNextTraversalSliceSample(Entity vehicle, TraversalSliceSamplingPlan plan, uint nowFrame)
        {
            if (vehicle == Entity.Null)
                return;

            uint intervalFrames = plan.Available
                ? math.max(1u, plan.SampleIntervalFrames)
                : TraversalSliceSampleIntervalLowFrames;
            uint baseFrame = nowFrame;
            if (m_Slices.Sessions.ContainsKey(vehicle)
                && m_Slices.NextSampleFrames.TryGetValue(vehicle, out uint scheduledFrame))
            {
                baseFrame = scheduledFrame;
            }
            uint nextFrame = baseFrame;
            do { nextFrame += intervalFrames; } while (nextFrame <= nowFrame);
            m_Slices.NextSampleFrames[vehicle] = nextFrame;
        }

        private void ScheduleNextTraversalSliceEntryProbe(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null)
                return;

            uint baseFrame = m_Slices.NextEntryProbeFrames.TryGetValue(vehicle, out uint scheduledFrame)
                ? scheduledFrame
                : nowFrame;
            uint nextFrame = baseFrame;
            do { nextFrame += TraversalSliceEntryProbeIntervalFrames; } while (nextFrame <= nowFrame);
            m_Slices.NextEntryProbeFrames[vehicle] = nextFrame;
        }

        internal bool TryBuildTraversalSliceSamplingPlan(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out TraversalSliceSamplingPlan plan)
        {
            return TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, null, out plan);
        }

        private bool TryBuildTraversalSliceSamplingPlan(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain knownChain, out TraversalSliceSamplingPlan plan)
        {
            plan = default;
            uint nowFrame = m_Port.Frame();
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line != line)
            {
                m_Slices.Plans.Remove(vehicle);
                return false;
            }

            LineTrackChain chain = knownChain;
            if (chain == null && !m_TrackModel.TryChain(line, out chain))
                chain = null;

            uint scheduledRefreshFrame = 0;
            if (chain != null
                && m_Slices.Plans.TryGetValue(vehicle, out TraversalSliceSamplingPlanCache cachedPlan)
                && cachedPlan.Line == line
                && cachedPlan.ChainSignature == chain.Signature
                && cachedPlan.SliceIndex == session.SliceIndex)
            {
                if (nowFrame < cachedPlan.NextRefreshFrame)
                {
                    plan = cachedPlan.Plan;
                    return true;
                }
                scheduledRefreshFrame = cachedPlan.NextRefreshFrame;
            }

            if (!m_TrackModel.TryProfile(line, out LineTraversalProfile profile)
                || chain == null
                || profile.SegmentSliceCutPointProgresses == null)
            {
                m_Slices.Plans.Remove(vehicle);
                return false;
            }

            if (!TryBuildTraversalSliceSamplingPlanUncached(vehicle, waypoints, chain, out plan))
            {
                m_Slices.Plans.Remove(vehicle);
                return false;
            }

            uint refreshFrames = math.max(1u, plan.SampleIntervalFrames);
            uint nextRefreshFrame = scheduledRefreshFrame != 0 ? scheduledRefreshFrame : nowFrame;
            do { nextRefreshFrame += refreshFrames; } while (nextRefreshFrame <= nowFrame);
            m_Slices.Plans[vehicle] = new TraversalSliceSamplingPlanCache(line, chain.Signature, session.SliceIndex, nextRefreshFrame, plan);
            return true;
        }

        private bool TryGetEligibleTraversalSliceChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            if (m_Slices.LineEligibility.TryGetValue(line, out TraversalSliceLineEligibilityCache cached)
                && cached.Line == line
                && nowFrame < cached.NextRefreshFrame)
            {
                if (!cached.Eligible)
                    return false;

                if (m_TrackModel.TryChain(line, out chain)
                    && chain != null
                    && chain.Signature == cached.ChainSignature
                    && chain.TraversalProfile != null
                    && chain.TraversalProfile.RunSlices.Count > 0)
                {
                    return true;
                }
            }

            if (!m_TrackModel.TryGetChainForLine(line, waypoints, out chain)
                || chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0)
            {
                m_Slices.LineEligibility[line] = new TraversalSliceLineEligibilityCache(
                    line,
                    0ul,
                    false,
                    nowFrame + TraversalSliceLineEligibilityNegativeCacheFrames);
                return false;
            }

            m_Slices.LineEligibility[line] = new TraversalSliceLineEligibilityCache(
                line,
                chain.Signature,
                true,
                nowFrame + TraversalSliceLineEligibilityNegativeCacheFrames);
            return true;
        }

        internal bool TryBuildTraversalSliceSamplingPlanUncached(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain chain, out TraversalSliceSamplingPlan plan)
        {
            plan = default;
            if (!m_Port.RouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return false;

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int segmentIndex = nextWaypointIndex == 0 ? math.max(0, waypoints.Length - 1) : nextWaypointIndex - 1;
            if (segmentIndex < 0
                || segmentIndex >= chain.TraversalProfile.SegmentSliceCutPointProgresses.Length)
            {
                return false;
            }

            uint sampleIntervalFrames = TraversalSliceSampleIntervalLowFrames;
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

                if (nearestCutPointDistance <= TraversalSliceSampleHighThreshold)
                {
                    isHighSampling = true;
                    sampleIntervalFrames = TraversalSliceSampleIntervalHighFrames;
                }
                else if (nearestCutPointDistance <= TraversalSliceSampleMediumThreshold)
                {
                    isMediumSampling = true;
                    sampleIntervalFrames = TraversalSliceSampleIntervalMediumFrames;
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

        internal void FinalizeVehicleTraversalSliceObservation(Entity vehicle, uint nowFrame, int exitAtomIndex = -1, float exitAtomPosition01 = 0f)
        {
            if (vehicle == Entity.Null
                || !m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line == Entity.Null
                || session.SliceIndex < 0
                || nowFrame <= session.EnterFrame)
            {
                m_Slices.Sessions.Remove(vehicle);
                m_Slices.Plans.Remove(vehicle);
                m_Slices.NextSampleFrames.Remove(vehicle);
                return;
            }

            float observedFrames = nowFrame - session.EnterFrame;
            m_Slices.RecordActualSample(new TraversalSliceActualSample(
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
            ulong key = Keys.Slice(session.Line, session.SliceIndex);
            if (m_Slices.TryObservation(key, out TraversalSliceObservation existing))
            {
                int sampleCount = existing.SampleCount + 1;
                float averageFrames = ((existing.AverageFrames * existing.SampleCount) + observedFrames) / sampleCount;
                float fastBaselineFrames = ComputeFastTraversalBaselineFrames(existing.FastBaselineFrames, observedFrames);
                TraversalSliceObservation updated = new TraversalSliceObservation(averageFrames, fastBaselineFrames, sampleCount, nowFrame);
                m_Slices.Record(session.Line, key, updated);
                m_Port.FlushSlice(session.Line, session.SliceIndex, updated);
                m_Admission.OnSliceWritten(session.Line);
            }
            else
            {
                TraversalSliceObservation created = new TraversalSliceObservation(observedFrames, observedFrames, 1, nowFrame);
                m_Slices.Record(session.Line, key, created);
                m_Port.FlushSlice(session.Line, session.SliceIndex, created);
                m_Admission.OnSliceWritten(session.Line);
            }

            m_Slices.Sessions.Remove(vehicle);
            m_Slices.Plans.Remove(vehicle);
            m_Slices.NextSampleFrames.Remove(vehicle);
        }

        internal void MaybeRecordTraversalPositionSample(Entity vehicle, Entity line, LineTrackChain chain, int sliceIndex, VehicleTrackCursor cursor, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return;

            const uint sampleIntervalFrames = 180u;
            if (m_Slices.LastPositionSampleFrames.TryGetValue(vehicle, out uint lastFrame)
                && nowFrame > lastFrame
                && nowFrame - lastFrame < sampleIntervalFrames)
            {
                return;
            }

            m_Slices.LastPositionSampleFrames[vehicle] = nowFrame;
            Entity physicalLane = Entity.Null;
            int atomIndex = math.clamp(cursor.AtomCursorIndex, 0, math.max(0, chain.TrackAtoms.Count - 1));
            if (atomIndex >= 0 && atomIndex < chain.TrackAtoms.Count)
                physicalLane = chain.TrackAtoms[atomIndex].Key.PhysicalLaneKey;

            int segmentIndex = cursor.SegmentIndex;
            float segmentPosition = -1f;
            if (m_Port.RouteProgress(vehicle, out int routeNextWaypointIndex, out float routeSegmentPosition))
            {
                segmentIndex = routeNextWaypointIndex == 0
                    ? math.max(0, chain.SegmentRanges.Count - 1)
                    : routeNextWaypointIndex - 1;
                segmentPosition = math.saturate(routeSegmentPosition);
            }

            float speedMetersPerSecond = m_Port.HasMoving(vehicle) ? m_Port.Speed(vehicle) : 0f;
            float odometerMeters = m_Port.HasOdo(vehicle) ? m_Port.Odo(vehicle) : -1f;

            m_Slices.RecordPositionSample(new TraversalPositionSample(
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

        internal static float ComputeFastTraversalBaselineFrames(float existingFastBaselineFrames, float observedFrames)
        {
            if (!(observedFrames > 0f))
                return existingFastBaselineFrames;

            if (!(existingFastBaselineFrames > 0f))
                return observedFrames;

            const float fastFollowAlpha = 0.35f;
            const float slowFollowAlpha = 0.05f;
            float alpha = observedFrames <= existingFastBaselineFrames ? fastFollowAlpha : slowFollowAlpha;
            return math.lerp(existingFastBaselineFrames, observedFrames, alpha);
        }

        internal bool TryGetCurrentTraversalRunSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            return TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, null, out chain, out sliceIndex, out cursor);
        }

        private bool TryGetCurrentTraversalRunSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain knownChain,
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            chain = null;
            sliceIndex = -1;
            cursor = default;
            chain = knownChain;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || (chain == null && !m_TrackModel.TryGetChainForLine(line, waypoints, out chain))
                || chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || !m_TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
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
            return sliceIndex >= 0 && sliceIndex < chain.TraversalProfile.RunSlices.Count;
        }

        internal bool TryGetEffectiveTraversalRunSliceFrames(Entity line, TraversalRunSlice slice, out float effectiveRunFrames)
        {
            effectiveRunFrames = math.max(0f, slice.RunFrames);
            if (line == Entity.Null || slice.SliceIndex < 0)
                return effectiveRunFrames > 0f;

            ulong key = Keys.Slice(line, slice.SliceIndex);
            if (m_Slices.TryObservation(key, out TraversalSliceObservation observation)
                && observation.SampleCount > 0
                && observation.FastBaselineFrames > 0f)
            {
                effectiveRunFrames = observation.FastBaselineFrames;
                return true;
            }

            return effectiveRunFrames > 0f;
        }

        internal void RecordTraversalSliceLapDebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (vehicle == Entity.Null || slice.SliceIndex < 0)
                return;

            float enterCoordinate = atomIndex + math.saturate(atomPosition01);
            float enterOffsetAtoms = math.max(0f, enterCoordinate - slice.StartAtomIndex);
            bool midSliceStart = enterOffsetAtoms > 0.05f;
            ulong key = Keys.SliceDebug(vehicle, slice.SliceIndex);
            if (!m_Slices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordStart(enterOffsetAtoms, midSliceStart);
            m_Slices.LapDebug[key] = aggregate;
        }

        internal void RecordTraversalSliceLapDebugDropped(Entity vehicle, int sliceIndex)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (vehicle == Entity.Null || sliceIndex < 0)
                return;

            ulong key = Keys.SliceDebug(vehicle, sliceIndex);
            if (!m_Slices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.DroppedWithoutFinalizeCount++;
            m_Slices.LapDebug[key] = aggregate;
        }

        internal void RecordTraversalSliceLapDebugFinalize(Entity vehicle, int sliceIndex, float observedFrames)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (vehicle == Entity.Null || sliceIndex < 0 || observedFrames <= 0f)
                return;

            ulong key = Keys.SliceDebug(vehicle, sliceIndex);
            if (!m_Slices.LapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordFinalize(observedFrames);
            m_Slices.LapDebug[key] = aggregate;
        }

        internal void ClearVehicleTraversalSliceLapDebug(Entity vehicle)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (vehicle == Entity.Null || m_Slices.LapDebug.Count == 0)
                return;

            List<ulong> removeKeys = null;
            foreach (var kvp in m_Slices.LapDebug)
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
                m_Slices.LapDebug.Remove(removeKeys[i]);
        }

        internal void BeginObservedDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || waypointIndex < 0)
                return;

            m_Dwell.Begin(vehicle, line, waypointIndex, nowFrame);
        }

        internal bool TryRecordObservedStopDwellOnBoardingEnd(
            Entity vehicle,
            Entity line,
            int fallbackWaypointIndex,
            uint nowFrame,
            out ObservedDwellSample sample)
        {
            sample = default;
            if (vehicle == Entity.Null || line == Entity.Null)
                return false;
            if (!m_Dwell.End(vehicle, out DwellSession session))
                return false;

            int waypointIndex = session.WaypointIndex >= 0 ? session.WaypointIndex : fallbackWaypointIndex;
            if (waypointIndex < 0 || session.Line != line || nowFrame <= session.StartFrame)
                return false;

            uint sampleFrames = nowFrame - session.StartFrame;
            if (sampleFrames == 0)
                return false;

            uint maxObservedDwellFrames = m_Port.ToFramesCeil(MaxObservedDwellMinutes);
            if (sampleFrames > maxObservedDwellFrames)
                return false;

            float sampleMinutes = (float)m_Port.ToMinutes(sampleFrames);
            sample = new ObservedDwellSample(line, waypointIndex, sampleFrames, nowFrame, sampleMinutes);
            return true;
        }

        internal void RecordStationDwellObservation(string observationKey, float sampleFrames, uint nowFrame)
        {
            if (string.IsNullOrWhiteSpace(observationKey))
                return;

            if (m_Dwell.TryStation(observationKey, out StationDwellObservation existing))
            {
                int sampleCount = math.min(existing.SampleCount + 1, 32);
                float averageFrames = existing.SampleCount <= 0
                    ? sampleFrames
                    : ((existing.AverageFrames * existing.SampleCount) + sampleFrames) / (existing.SampleCount + 1);
                StationDwellObservation updated = new StationDwellObservation
                {
                    AverageFrames = averageFrames,
                    SampleCount = sampleCount,
                    LastObservedFrame = nowFrame
                };
                m_Dwell.RecordStation(observationKey, updated);
                m_Port.FlushStationDwell(observationKey, updated);
                return;
            }

            StationDwellObservation created = new StationDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1,
                LastObservedFrame = nowFrame
            };
            m_Dwell.RecordStation(observationKey, created);
            m_Port.FlushStationDwell(observationKey, created);
        }

        internal bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, string stationAnchorId, out float dwellFrames)
        {
            dwellFrames = 0f;
            string observationKey = StationDwellKey(line, stationAnchorId);
            if (!string.IsNullOrWhiteSpace(observationKey)
                && m_Dwell.TryStation(observationKey, out StationDwellObservation anchorObservation)
                && anchorObservation.AverageFrames > 0f
                && anchorObservation.SampleCount > 0)
            {
                dwellFrames = anchorObservation.AverageFrames;
                return dwellFrames > 0f;
            }

            return false;
        }

        internal bool TryStationDwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            anchor = default;
            if (line == Entity.Null
                || !m_Port.Exists(line)
                || waypointIndex < 0
                || !m_Port.HasWaypoints(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Port.Waypoints(line);
            if (waypointIndex >= waypoints.Length)
                return false;

            Entity waypoint = waypoints[waypointIndex].m_Waypoint;
            Entity stopEntity = m_Port.Stop(waypoint);
            if (stopEntity == Entity.Null)
                return false;

            Entity anchorEntity = m_Port.Anchor(waypoint);
            if (anchorEntity == Entity.Null)
                anchorEntity = m_Port.AnchorFromStop(stopEntity);
            if (anchorEntity == Entity.Null)
                return false;

            string stationAnchorId = m_Port.EnsureSak(anchorEntity);
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !RapidTransitMod.Stops.IsKey(stationAnchorId))
                return false;

            Entity buildingEntity = m_Port.StationOf(stopEntity);
            if (buildingEntity == Entity.Null)
                buildingEntity = m_Port.ResolveStation(stopEntity);

            anchor = new StationDwellAnchor(stationAnchorId, anchorEntity, stopEntity, buildingEntity);
            return true;
        }

        internal string StationDwellKey(Entity line, string stationAnchorId)
        {
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !RapidTransitMod.Stops.IsKey(stationAnchorId))
                return string.Empty;

            string lineId = m_Port.LineId(line);
            if (string.IsNullOrWhiteSpace(lineId))
                lineId = line != Entity.Null ? "entity:" + line.Index.ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            return lineId + "|" + stationAnchorId;
        }

        internal static bool IsStationDwellKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int separatorIndex = value.IndexOf('|');
            return separatorIndex > 0
                && separatorIndex + 1 < value.Length
                && RapidTransitMod.Stops.IsKey(value.Substring(separatorIndex + 1));
        }

        private void LogTraversalProfileLapSlices(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return;
            }

            m_TrackModel.EnsureBypassPipelineReady(chain);
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
                ulong observationKey = Keys.Slice(line, slice.SliceIndex);
                bool hasObservation = m_Slices.TryObservation(observationKey, out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;
                ulong lapDebugKey = Keys.SliceDebug(vehicle, slice.SliceIndex);
                bool hasLapDebug = m_Slices.LapDebug.TryGetValue(lapDebugKey, out TraversalSliceLapDebugAggregate lapDebug);
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
                        lapDebugText += " obsLapAvg=" + m_Port.ToMinutes(lapDebug.ObservedFramesSum / lapDebug.FinalizeCount).ToString("0.0") + "min"
                            + " obsLapMin=" + m_Port.ToMinutes(lapDebug.MinObservedFrames).ToString("0.0") + "min"
                            + " obsLapMax=" + m_Port.ToMinutes(lapDebug.MaxObservedFrames).ToString("0.0") + "min";
                    }
                }

                m_Port.Log("[ExpressLapSlices] " + lineTag + " vehicle" + vehicle.Index
                    + " slice#" + slice.SliceIndex
                    + " " + startLabel + " -> " + endLabel
                    + " run=" + m_Port.ToMinutes(math.max(0f, effectiveRunFrames)).ToString("0.0") + "min"
                    + " staticRun=" + m_Port.ToMinutes(staticRunFrames).ToString("0.0") + "min"
                    + " stop=" + m_Port.ToMinutes(stopFrames).ToString("0.0") + "min"
                    + " total=" + m_Port.ToMinutes(math.max(0f, effectiveRunFrames) + stopFrames).ToString("0.0") + "min"
                    + " atoms=" + slice.StartAtomIndex + ".." + slice.EndAtomIndexExclusive
                    + " laneKeys=" + (slice.PhysicalLaneKeys != null ? slice.PhysicalLaneKeys.Length : 0)
                    + " obsGlobal=" + (hasObservation ? observation.SampleCount.ToString() : "0")
                    + (hasObservation
                        ? " obsGlobalAvg=" + m_Port.ToMinutes(observation.AverageFrames).ToString("0.0") + "min"
                        : string.Empty)
                    + (hasObservation && observation.FastBaselineFrames > 0f
                        ? " obsGlobalFast=" + m_Port.ToMinutes(observation.FastBaselineFrames).ToString("0.0") + "min"
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
                        ? m_Port.Name(traversalEvent.Building)
                        : "atom" + atomIndex;
                    if (string.IsNullOrEmpty(buildingLabel))
                        buildingLabel = traversalEvent.Building != Entity.Null
                            ? "#" + traversalEvent.Building.Index
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
                        case TraversalEventKind.OutsideEndpointBoundary:
                            return "OutsideEndpoint(" + buildingLabel + ")";
                    }
                }
            }

            return "atom" + atomIndex;
        }

        private static string SlotStr(int minutes)
        {
            if (minutes < 0) return "-";
            minutes %= 1440;
            if (minutes < 0) minutes += 1440;
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }
    }
}
