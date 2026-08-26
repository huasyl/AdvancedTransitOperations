using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;
namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineStructureInvalidator
    {
        private const uint LayoutRetryFrames = 16u;
        private const byte LayoutRetryLimit = 3;
        private const uint CandidateQuietFrames = 80u;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, PendingRoadInvalidation> m_PendingRoadLines = new Dictionary<Entity, PendingRoadInvalidation>();
        private readonly Dictionary<Entity, RailStructureState> m_RailStates =
            new Dictionary<Entity, RailStructureState>();
        private readonly HashSet<Entity> m_ActiveRailLines = new HashSet<Entity>();
        private readonly List<RailStructureState> m_RailDrainStates =
            new List<RailStructureState>();
        private uint m_NextLayoutRetryFrame;
        private bool m_ObservationRestored;
        private enum RailPhase : byte
        {
            Candidate = 0,
            Stable = 1,
            WaitingFinal = 2,
            WaitingRecovery = 3,
            MissingBaseline = 4,
            Deleted = 5
        }
        private sealed class RailStructureState
        {
            internal Entity Line;
            internal string LineId = string.Empty, Mode = string.Empty;
            internal RailPhase Phase;
            internal LineTrackChain StableChain, CandidateChain;
            internal LineStopLayout StableLayout;
            internal string PendingOldStopSig = string.Empty;
            internal uint LastCandidateFrame, CandidateRevision;
            internal bool PendingApplied;
            internal LineStructurePlan PendingPlan;
            internal int PendingReleasedBypass;
        }
        private readonly struct RailCleanupSummary
        {
            internal readonly int InvalidatedOpenSamples, TimedPlanNone, TimedPlanReprojected, TimedPlanCleared;
            internal readonly SliceLineClearResult Slices;
            internal readonly MonitorAverageRemapResult MonitorAverage;
            internal readonly LineTimesChangeResult LineTimes;
            internal readonly LineMileageChangeResult LineMileage;
            internal readonly int ClearedProjection, ClearedLap, LineContextCleared, VehicleContextCleared, ReleasedBypass, TargetVehicles;
            internal RailCleanupSummary(
                int invalidatedOpenSamples,
                SliceLineClearResult slices,
                MonitorAverageRemapResult monitorAverage,
                LineTimesChangeResult lineTimes,
                LineMileageChangeResult lineMileage,
                int timedPlanNone,
                int timedPlanReprojected,
                int timedPlanCleared,
                int clearedProjection,
                int clearedLap,
                int lineContextCleared,
                int vehicleContextCleared,
                int releasedBypass,
                int targetVehicles)
            {
                InvalidatedOpenSamples = invalidatedOpenSamples; Slices = slices; MonitorAverage = monitorAverage;
                LineTimes = lineTimes; LineMileage = lineMileage; TimedPlanNone = timedPlanNone;
                TimedPlanReprojected = timedPlanReprojected; TimedPlanCleared = timedPlanCleared;
                ClearedProjection = clearedProjection; ClearedLap = clearedLap; LineContextCleared = lineContextCleared;
                VehicleContextCleared = vehicleContextCleared; ReleasedBypass = releasedBypass; TargetVehicles = targetVehicles;
            }
        }
        private readonly struct PendingRoadInvalidation
        {
            internal readonly Entity Line;
            internal readonly string LineId;
            internal readonly string Mode;
            internal readonly LineProfile.RoadRouteSnapshot OldRoute;
            internal readonly LineProfile.RoadRouteSnapshot NewRoute;
            internal readonly uint NextRetryFrame;
            internal readonly byte RetryCount;
            internal PendingRoadInvalidation(
                Entity line,
                string lineId,
                string mode,
                LineProfile.RoadRouteSnapshot oldRoute,
                LineProfile.RoadRouteSnapshot newRoute,
                uint nextRetryFrame = 0,
                byte retryCount = 0)
            {
                Line = line;
                LineId = lineId ?? string.Empty;
                Mode = mode ?? string.Empty;
                OldRoute = oldRoute;
                NewRoute = newRoute;
                NextRetryFrame = nextRetryFrame;
                RetryCount = retryCount;
            }
            internal PendingRoadInvalidation WithLatest(LineProfile.RoadRouteSnapshot newRoute)
            {
                return new PendingRoadInvalidation(Line, LineId, Mode, OldRoute, newRoute);
            }
            internal PendingRoadInvalidation WithRetry(uint frame)
            {
                byte count = (byte)(RetryCount + 1);
                return new PendingRoadInvalidation(
                    Line,
                    LineId,
                    Mode,
                    OldRoute,
                    NewRoute,
                    frame + LayoutRetryFrames,
                    count);
            }
        }
        internal LineStructureInvalidator(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }
        internal bool IsLinePending(Entity line)
        {
            if (line == Entity.Null
                || !m_RailStates.TryGetValue(line, out RailStructureState state))
            {
                return false;
            }
            return state.Phase == RailPhase.WaitingFinal
                || state.Phase == RailPhase.WaitingRecovery
                || state.Phase == RailPhase.MissingBaseline
                || state.Phase == RailPhase.Deleted;
        }
        internal void RequestCandidate(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null)
                return;
            RailStructureState state = GetRailState(line);
            if (IsDeletedLine(line))
            {
                MarkDeleted(state);
                return;
            }
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (state.Phase == RailPhase.Deleted)
                return;
            if (state.PendingPlan != null)
            {
                if (SameCandidate(state.CandidateChain, chain))
                    return;
                state.PendingPlan = null;
                state.CandidateChain = chain;
                state.CandidateRevision++;
                state.LastCandidateFrame = frame;
                state.Phase = IsCompleteChain(chain)
                    ? RailPhase.WaitingFinal
                    : RailPhase.WaitingRecovery;
                ActivateRail(line);
                return;
            }
            if (!state.PendingApplied && state.Phase != RailPhase.Stable
                && state.Phase != RailPhase.WaitingFinal
                && state.Phase != RailPhase.WaitingRecovery
                && state.Phase != RailPhase.MissingBaseline)
            {
                if (!ReferenceEquals(state.CandidateChain, chain))
                {
                    state.CandidateChain = chain;
                    state.CandidateRevision++;
                    state.LastCandidateFrame = frame;
                }
                return;
            }
            LineTrackChain previousCandidate = state.Phase == RailPhase.Stable
                ? state.StableChain
                : state.CandidateChain;
            bool changed = !SameCandidate(previousCandidate, chain);
            if (changed)
            {
                state.CandidateChain = chain;
                state.CandidateRevision++;
                state.LastCandidateFrame = frame;
            }
            if (state.Phase == RailPhase.Stable && changed)
            {
                state.Phase = chain != null && chain.ChainComplete
                    ? RailPhase.WaitingFinal
                    : RailPhase.WaitingRecovery;
                ActivateRail(line);
                EnterPending(state, "candidate-changed");
            }
            else if ((state.Phase == RailPhase.WaitingFinal
                    || state.Phase == RailPhase.WaitingRecovery)
                && chain == null)
            {
                state.Phase = RailPhase.WaitingRecovery;
            }
        }
        internal void ObserveChainEstablished(Entity line, LineTrackChain chain)
        {
            RequestCandidate(line, chain);
        }
        internal void ConfirmLineDeleted(TrackLineDeletedFact fact)
        {
            if (fact.Line == Entity.Null || fact.Mode == TransitMode.Unknown)
                return;

            if (m_RailStates.TryGetValue(fact.Line, out RailStructureState state))
            {
                MarkDeleted(state);
                return;
            }

            if (fact.LineKey.IsEmpty)
                return;

            state = CreateRailState(
                fact.Line,
                LineIdentityService.GetId(fact.LineKey),
                fact.Mode);
            m_RailStates[fact.Line] = state;
            ActivateRail(fact.Line);
            MarkDeleted(state);
        }
        internal void ResetRuntimeState()
        {
            m_RailStates.Clear();
            m_ActiveRailLines.Clear();
            m_RailDrainStates.Clear();
            m_ObservationRestored = false;
            m_NextLayoutRetryFrame = 0;
            m_PendingRoadLines.Clear();
        }
        internal void OnObservationRestored()
        {
            m_ObservationRestored = true;
            foreach (RailStructureState state in m_RailStates.Values)
            {
                if (state.Phase == RailPhase.WaitingFinal
                    || state.Phase == RailPhase.WaitingRecovery
                    || state.Phase == RailPhase.MissingBaseline
                    || state.Phase == RailPhase.Deleted)
                {
                    EnterPending(state, "observation-restored");
                }
            }
        }
        internal List<PendingLineStructureRecord> ExportPending()
        {
            List<PendingLineStructureRecord> records = new List<PendingLineStructureRecord>();
            foreach (RailStructureState state in m_RailStates.Values)
            {
                if (state.Phase != RailPhase.WaitingFinal
                    && state.Phase != RailPhase.WaitingRecovery
                    && state.Phase != RailPhase.MissingBaseline)
                {
                    continue;
                }
                records.Add(new PendingLineStructureRecord(
                    state.Line,
                    state.LineId,
                    state.StableLayout != null
                        ? state.StableLayout.StopSig
                        : state.PendingOldStopSig,
                    true));
            }
            return records;
        }
        internal void RestorePending(IReadOnlyList<PendingLineStructureRecord> records)
        {
            if (records == null)
                return;
            for (int i = 0; i < records.Count; i++)
            {
                PendingLineStructureRecord record = records[i];
                Entity line = record.Line;
                if (!LineKey.TryParse(record.LineId, out LineKey key)
                    || !m_Runtime.m_LineAnchorCatalog.TryEntity(key, out Entity anchoredLine))
                {
                    continue;
                }
                line = anchoredLine;
                RailStructureState state = GetRailState(line);
                state.Phase = RailPhase.MissingBaseline;
                state.StableChain = null;
                state.CandidateChain = null;
                state.StableLayout = null;
                state.PendingOldStopSig = record.OldStopSig;
                // A persisted pending marker always loses its in-memory old chain on load.
                state.CandidateRevision = 0;
                state.LastCandidateFrame = m_Runtime.m_SimulationSystem.frameIndex;
                state.PendingApplied = false;
                state.PendingPlan = null;
                ActivateRail(line);
            }
        }
        private RailStructureState GetRailState(Entity line)
        {
            if (!m_RailStates.TryGetValue(line, out RailStructureState state))
            {
                state = CreateRailState(
                    line,
                    m_Runtime.LineStableId(line),
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line));
                m_RailStates[line] = state;
                ActivateRail(line);
            }
            return state;
        }

        private static RailStructureState CreateRailState(
            Entity line,
            string lineId,
            TransitMode mode)
        {
            return new RailStructureState
            {
                Line = line,
                LineId = lineId ?? string.Empty,
                Mode = TransitModeCodec.Format(mode),
                Phase = RailPhase.Candidate
            };
        }
        private void ActivateRail(Entity line)
        {
            if (line != Entity.Null)
                m_ActiveRailLines.Add(line);
        }
        private void DeactivateRail(Entity line)
        {
            if (line != Entity.Null)
                m_ActiveRailLines.Remove(line);
        }
        private static bool SameCandidate(LineTrackChain left, LineTrackChain right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return left == right;
            return false;
        }
        private bool IsDeletedLine(Entity line)
        {
            return line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || m_Runtime.EntityManager.HasComponent<Deleted>(line);
        }
        private void MarkDeleted(RailStructureState state)
        {
            if (state == null)
                return;
            if (state.Phase == RailPhase.Deleted && state.PendingPlan != null)
            {
                EnterPending(state, "line-deleted");
                return;
            }
            state.CandidateChain = null;
            state.LastCandidateFrame = m_Runtime.m_SimulationSystem.frameIndex;
            state.Phase = RailPhase.Deleted;
            state.PendingPlan = null;
            ActivateRail(state.Line);
            EnterPending(state, "line-deleted");
        }
        private void EnterPending(RailStructureState state, string reason)
        {
            if (state == null || state.PendingApplied || !m_ObservationRestored)
                return;
            int invalidatedSamples = m_Runtime.m_Observation.SuspendLine(
                state.Line,
                out int endedSlices);
            int releasedBypass = m_Runtime.m_Bypass.ReleaseLineForPending(state.Line);
            state.PendingReleasedBypass = releasedBypass;
            state.PendingApplied = true;
            m_Runtime.log.Info("[LineStructurePending] line=" + state.Line.Index
                + " lineId=" + (state.LineId ?? string.Empty)
                + " reason=" + (reason ?? "pending")
                + " invalidatedSamples=" + invalidatedSamples
                + " endedSliceSessions=" + endedSlices
                + " releasedBypass=" + releasedBypass
                + " OpenIntervalMaxFrames=0"
                + " historicalAveragesCleared=0"
                + " dwellCleared=0"
                + " originalVehiclePathsTouched=0");
        }
        private bool TryReadRailLayout(Entity line, out LineStopLayout layout)
        {
            layout = null;
            if (m_Runtime.m_RoutePlans == null
                || !m_Runtime.m_RoutePlans.TryGet(line, LifecycleKind.Rail, out RoutePlan plan)
                || !LineStopLayout.TryCreate(
                    plan,
                    RuntimeRoot.StopService(m_Runtime).StationName,
                    out layout))
            {
                return false;
            }
            return true;
        }
        private static bool IsCompleteChain(LineTrackChain chain)
        {
            if (chain == null || !chain.ChainComplete || chain.TrackAtoms.Count == 0
                || chain.SegmentRanges == null || chain.SegmentRanges.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < chain.SegmentRanges.Count; i++)
            {
                TrackSegmentRange range = chain.SegmentRanges[i];
                if (range.StartAtomIndex < 0
                    || range.EndAtomIndexExclusive <= range.StartAtomIndex
                    || range.EndAtomIndexExclusive > chain.TrackAtoms.Count)
                {
                    return false;
                }
            }
            return true;
        }
        internal void RequestRoadRoute(
            Entity line,
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            if (line == Entity.Null || oldRoute == null || newRoute == null || !m_Runtime.m_SystemReady)
                return;
            if (m_PendingRoadLines.TryGetValue(line, out PendingRoadInvalidation pending))
            {
                m_PendingRoadLines[line] = pending.WithLatest(newRoute);
                m_NextLayoutRetryFrame = 0;
                return;
            }
            m_PendingRoadLines[line] = new PendingRoadInvalidation(
                line,
                m_Runtime.LineStableId(line),
                TransitModeCodec.Format(TransportModeResolver.Resolve(m_Runtime.EntityManager, line)),
                oldRoute,
                newRoute);
            m_NextLayoutRetryFrame = 0;
        }
        internal void Drain()
        {
            if (m_PendingRoadLines.Count == 0 && m_ActiveRailLines.Count == 0)
                return;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_NextLayoutRetryFrame != 0 && frame < m_NextLayoutRetryFrame)
                return;
            m_NextLayoutRetryFrame = 0;
            m_RailDrainStates.Clear();
            foreach (Entity line in m_ActiveRailLines)
            {
                if (m_RailStates.TryGetValue(line, out RailStructureState state))
                    m_RailDrainStates.Add(state);
            }
            for (int i = 0; i < m_RailDrainStates.Count; i++)
                DrainRailState(m_RailDrainStates[i], frame);
            if (m_PendingRoadLines.Count == 0)
                return;
            List<PendingRoadInvalidation> roadLines = new List<PendingRoadInvalidation>(m_PendingRoadLines.Values);
            m_PendingRoadLines.Clear();
            for (int i = 0; i < roadLines.Count; i++)
                DrainRoadLine(roadLines[i]);
        }
        private void DrainRailState(RailStructureState state, uint frame)
        {
            if (state == null)
                return;
            if (state.Phase != RailPhase.Deleted && IsDeletedLine(state.Line))
            {
                MarkDeleted(state);
                return;
            }
            if (state.Phase == RailPhase.Candidate)
            {
                if (!m_Runtime.m_SystemReady)
                    return;
                if (!IsCompleteChain(state.CandidateChain))
                    return;
                if (frame - state.LastCandidateFrame < CandidateQuietFrames)
                    return;
                if (!TryReadRailLayout(state.Line, out LineStopLayout layout))
                    return;
                state.StableChain = state.CandidateChain;
                state.StableLayout = layout;
                state.PendingOldStopSig = string.Empty;
                state.Phase = RailPhase.Stable;
                state.PendingApplied = false;
                DeactivateRail(state.Line);
                return;
            }
            if (state.Phase != RailPhase.WaitingFinal
                && state.Phase != RailPhase.WaitingRecovery
                && state.Phase != RailPhase.MissingBaseline
                && state.Phase != RailPhase.Deleted)
            {
                return;
            }
            if (!m_Runtime.m_SystemReady)
                return;
            if (!m_ObservationRestored)
                return;
            if (state.Phase == RailPhase.Deleted)
            {
                if (state.PendingPlan == null)
                    state.PendingPlan = BuildDeletedPlan(state);
                else
                    CommitRailPlan(state, frame);
                return;
            }
            if (state.PendingPlan != null)
            {
                CommitRailPlan(state, frame);
                return;
            }
            if (state.CandidateChain == null || !IsCompleteChain(state.CandidateChain))
            {
                state.Phase = RailPhase.WaitingRecovery;
                return;
            }
            if (frame - state.LastCandidateFrame < CandidateQuietFrames)
                return;
            if (!TryReadRailLayout(state.Line, out LineStopLayout finalLayout))
            {
                state.Phase = RailPhase.WaitingRecovery;
                return;
            }
            state.PendingPlan = BuildRailPlan(state, finalLayout);
            if (state.Phase == RailPhase.WaitingRecovery)
                state.Phase = RailPhase.WaitingFinal;
        }
        private bool PlanMatches(RailStructureState state, LineStructurePlan plan)
        {
            if (state == null || plan == null || plan.Line != state.Line
                || !string.Equals(plan.LineId, state.LineId, System.StringComparison.Ordinal)
                || plan.CandidateRevision != state.CandidateRevision)
            {
                return false;
            }
            if (plan.Kind == LineStructurePlanKind.LineDeleted)
            {
                return state.CandidateChain == null
                    && plan.NewChainFact.Line == Entity.Null
                    && plan.NewLayout == null;
            }
            return plan.NewChainFact.Matches(state.CandidateChain);
        }
        private void ResetStalePlan(RailStructureState state, uint frame)
        {
            state.PendingPlan = null;
            state.LastCandidateFrame = frame;
            if (state.Phase == RailPhase.Deleted)
                return;
            state.Phase = IsCompleteChain(state.CandidateChain)
                ? RailPhase.WaitingFinal
                : RailPhase.WaitingRecovery;
            ActivateRail(state.Line);
        }
        private void CommitRailPlan(RailStructureState state, uint frame)
        {
            if (!m_Runtime.m_SystemReady || !m_ObservationRestored || state?.PendingPlan == null)
                return;
            LineStructurePlan plan = state.PendingPlan;
            if (!PlanMatches(state, plan))
            {
                ResetStalePlan(state, frame);
                return;
            }
            RailCleanupSummary summary = ApplyRailCleanup(plan, frame, state.PendingReleasedBypass);
            FinishRailPlan(state, plan);
            NotifyRailCleanup(plan);
            LogRailCleanup(plan, summary);
        }
        private RailCleanupSummary ApplyRailCleanup(
            LineStructurePlan plan,
            uint frame,
            int releasedBypass)
        {
            int invalidatedOpenSamples = m_Runtime.m_Observation.InvalidateLineOpenIntervals(plan.Line);
            SliceLineClearResult slices = m_Runtime.m_Observation.InvalidateSliceLine(plan.Line);
            MonitorAverageRemapResult monitorAverage;
            if (plan.Kind == LineStructurePlanKind.Precise)
            {
                monitorAverage = m_Runtime.m_Observation.RemapMonitorAverage(
                    plan.Line,
                    plan.NewLayout,
                    plan.Impact,
                    plan.OldStopSig);
            }
            else
            {
                int removed = m_Runtime.m_Observation.RemoveMonitorAverage(plan.Line);
                monitorAverage = new MonitorAverageRemapResult(removed, 0, removed);
            }
            DynamicBuffer<RouteWaypoint> waypoints = default;
            bool hasWaypoints = plan.Kind != LineStructurePlanKind.LineDeleted
                && m_Runtime.EntityManager.Exists(plan.Line)
                && m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(plan.Line);
            if (hasWaypoints)
                waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(plan.Line, true);
            LineTimesChangeResult lineTimes = m_Runtime.m_LineTimes.ApplyStructure(
                plan.Line,
                waypoints,
                plan.LineTimes,
                plan.NewLayout,
                plan.Impact);
            LineMileageChangeResult lineMileage = m_Runtime.m_LineMileage.InvalidateLine(plan.Line);
            int timedPlanNone = 0;
            int timedPlanReprojected = 0;
            int timedPlanCleared = 0;
            int targetVehicles = 0;
            int clearedProjection = 0;
            int clearedLap = 0;
            int vehicleContextCleared = 0;
            bool releaseMonitorLine = plan.NewLayout == null;
            if (releaseMonitorLine)
                m_Runtime.m_Observation.ReleaseLineMonitor(plan.Line, frame);
            bool canBypass = TransportModeProfile.GetProfile(
                plan.NewChainFact.Mode != TransitMode.Unknown
                    ? plan.NewChainFact.Mode
                    : plan.OldChainFact.Mode).CanBypass;
            int[] newWaypointIndices = CopyWaypointIndices(plan.NewLayout);
            for (int i = 0; i < plan.VehicleCount; i++)
            {
                Entity vehicle = plan.VehicleAt(i);
                if (vehicle == Entity.Null
                    || !m_Runtime.EntityManager.Exists(vehicle)
                    || !m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                    || vehicleLine != plan.Line)
                    continue;
                targetVehicles++;
                TimedPlanChangeKind timedChange;
                bool preservePlan = plan.NewLayout != null
                    && plan.Timetable == TimetablePlanKind.Preserve;
                if (preservePlan || plan.Kind == LineStructurePlanKind.Precise)
                {
                    timedChange = m_Runtime.m_StopRuntime.ReprojectTimedPlanForStructure(
                        vehicle,
                        plan.NewStopSig,
                        newWaypointIndices,
                        frame);
                    if (plan.NewLayout != null)
                    {
                        m_Runtime.m_Observation.SuppressMonitor(
                            vehicle,
                            plan.NewStopSig,
                            newWaypointIndices,
                            frame);
                    }
                }
                else
                {
                    timedChange = m_Runtime.m_StopRuntime.ClearTimedPlanForStructure(vehicle)
                        ? TimedPlanChangeKind.Cleared
                        : TimedPlanChangeKind.None;
                    if (plan.NewLayout != null)
                    {
                        m_Runtime.m_Observation.SuppressMonitor(
                            vehicle,
                            plan.NewStopSig,
                            newWaypointIndices,
                            frame);
                    }
                }
                switch (timedChange)
                {
                    case TimedPlanChangeKind.Reprojected:
                        timedPlanReprojected++;
                        break;
                    case TimedPlanChangeKind.Cleared:
                        timedPlanCleared++;
                        break;
                    default:
                        timedPlanNone++;
                        break;
                }
                m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, -1);
                m_Runtime.m_RailEventSource.InvalidateVehicleForStructure(vehicle);
                m_Runtime.m_CachedWpIdx.Remove(vehicle);
                m_Runtime.m_WaypointIndex.Remove(vehicle);
                m_Runtime.m_RouteProgress.Remove(vehicle);
                if (m_Runtime.m_TrackProjection.ClearVehicleForStructure(vehicle))
                    clearedProjection++;
                if (m_Runtime.m_ObsPersist.ClearLapForStructure(vehicle))
                    clearedLap++;
                m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
                if (m_Runtime.m_StationContextQuery.RemoveVehicle(vehicle))
                    vehicleContextCleared++;
                m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Stop);
                if (plan.Kind != LineStructurePlanKind.LineDeleted && canBypass)
                    m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Bypass);
            }
            int lineContextCleared = 0;
            if (m_Runtime.m_TrackProjection.ClearLineRunningVehicleSnapshots(plan.Line))
                lineContextCleared++;
            lineContextCleared += m_Runtime.m_LineView.InvalidateLine(plan.Line);
            if (m_Runtime.m_StationContextQuery.RemoveLine(plan.Line))
                lineContextCleared++;
            m_Runtime.m_RailEventSource.InvalidateLine(plan.Line);
            m_Runtime.m_LapCache.RemoveLine(plan.Line);
            m_Runtime.m_DispatchCache.RemoveLine(plan.Line);
            m_Runtime.m_TrackModel.InvalidateWaypointIndexLookup(plan.Line);
            if (plan.Kind == LineStructurePlanKind.LineDeleted && hasWaypoints)
                m_Runtime.m_LineProfile.RemoveStability(plan.Line, waypoints);
            else if (hasWaypoints)
                m_Runtime.m_LineProfile.ResetRailStructure(plan.Line, waypoints);
            else
                m_Runtime.m_LineProfile.RemoveStability(plan.Line);
            m_Runtime.m_Bypass.ClearLine(plan.Line);
            m_Runtime.m_TrackModel.MarkSharedIndexDirty();
            return new RailCleanupSummary(
                invalidatedOpenSamples,
                slices,
                monitorAverage,
                lineTimes,
                lineMileage,
                timedPlanNone,
                timedPlanReprojected,
                timedPlanCleared,
                clearedProjection,
                clearedLap,
                lineContextCleared,
                vehicleContextCleared,
                releasedBypass,
                targetVehicles);
        }
        private static int[] CopyWaypointIndices(LineStopLayout layout)
        {
            if (layout == null || layout.StopCount == 0)
                return System.Array.Empty<int>();
            int[] indices = new int[layout.StopCount];
            for (int i = 0; i < indices.Length; i++)
                indices[i] = layout[i].WaypointIndex;
            return indices;
        }
        private void FinishRailPlan(RailStructureState state, LineStructurePlan plan)
        {
            state.PendingPlan = null;
            state.PendingApplied = false;
            state.PendingReleasedBypass = 0;
            if (plan.Kind == LineStructurePlanKind.LineDeleted)
            {
                m_ActiveRailLines.Remove(state.Line);
                m_RailStates.Remove(state.Line);
                return;
            }
            state.StableChain = state.CandidateChain;
            state.StableLayout = plan.NewLayout;
            state.PendingOldStopSig = string.Empty;
            state.Phase = RailPhase.Stable;
            DeactivateRail(state.Line);
        }
        private void NotifyRailCleanup(LineStructurePlan plan)
        {
            bool clearDetails = plan.Kind == LineStructurePlanKind.LineDeleted
                || (plan.Timetable == TimetablePlanKind.Reevaluate
                    && string.IsNullOrEmpty(plan.NewStopSig));
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                plan.Line,
                plan.LineId,
                plan.Mode,
                plan.NewStopSig,
                plan.Kind == LineStructurePlanKind.LineDeleted ? "line-deleted" : "structure-changed",
                clearDetails,
                true);
        }
        private void LogRailCleanup(LineStructurePlan plan, RailCleanupSummary summary)
        {
            string reason = plan.Kind switch
            {
                LineStructurePlanKind.Precise => "precise",
                LineStructurePlanKind.CrossSaveFallback => "cross-save-fallback",
                LineStructurePlanKind.LineDeleted => "line-deleted",
                _ => "safety-fallback"
            };
            string affectedPairs = FormatAffectedPairs(plan);
            m_Runtime.log.Info("[LineStructureCleanup] line=" + plan.Line.Index
                + " lineId=" + plan.LineId
                + " mode=" + plan.Mode
                + " reason=" + reason
                + " oldSignature=" + plan.OldChainFact.Signature
                + " newSignature=" + plan.NewChainFact.Signature
                + " oldStopSig=" + plan.OldStopSig
                + " newStopSig=" + plan.NewStopSig
                + " stopSigChanged=" + plan.StopSigChanged
                + " affectedOld=" + plan.AffectedOldIntervalCount
                + " affectedNew=" + plan.AffectedNewIntervalCount
                + " retained=" + plan.RetainedIntervalCount
                + " endedSliceSessions=" + summary.Slices.EndedSessions
                + " sliceAdmissions=" + summary.Slices.RemovedAdmissions
                + " removedMemorySlices=" + summary.Slices.RemovedMemoryObservations
                + " removedPersistedSlices=" + summary.Slices.RemovedPersistedObservations
                + " zeroedMonitorIntervals=" + summary.MonitorAverage.ZeroedIntervals
                + " retainedMonitorIntervals=" + summary.MonitorAverage.RetainedIntervals
                + " affectedPairs=" + affectedPairs
                + " invalidatedOpenSamples=" + summary.InvalidatedOpenSamples
                + " releasedBypass=" + summary.ReleasedBypass
                + " lineTimes=" + summary.LineTimes.Kind.ToString().ToLowerInvariant()
                + " lineTimesApplied=" + summary.LineTimes.Applied
                + " lineMileage=" + (summary.LineMileage.SharedRevisionAdvanced
                    ? "remove+shared-revision"
                    : "remove")
                + " plannedVehicles=" + plan.VehicleCount
                + " targetVehicles=" + summary.TargetVehicles
                + " timedPlanNone=" + summary.TimedPlanNone
                + " timedPlanReprojected=" + summary.TimedPlanReprojected
                + " timedPlanCleared=" + summary.TimedPlanCleared
                + " clearedProjection=" + summary.ClearedProjection
                + " clearedLap=" + summary.ClearedLap
                + " lineContextCleared=" + summary.LineContextCleared
                + " vehicleContextCleared=" + summary.VehicleContextCleared
                + " dwellCleared=0 otherLinesTouched=0 globalClear=0");
        }
        private static string FormatAffectedPairs(LineStructurePlan plan)
        {
            if (plan == null)
                return "[]";

            bool precise = plan.Kind == LineStructurePlanKind.Precise;
            LineStopLayout layout = precise ? plan.NewLayout : plan.OldLayout;
            if (layout == null || layout.StopCount < 2)
                return "[unknown]";

            System.Text.StringBuilder pairs = new System.Text.StringBuilder("[");
            int written = 0;
            for (int i = 0; i < layout.StopCount; i++)
            {
                if (precise && (plan.Impact == null || !plan.Impact.IsNewAffected(i)))
                    continue;
                if (written++ > 0)
                    pairs.Append(',');
                AppendStopLabel(pairs, layout[i]);
                pairs.Append('>');
                AppendStopLabel(pairs, layout[(i + 1) % layout.StopCount]);
            }
            pairs.Append(']');
            return pairs.ToString();
        }
        private static void AppendStopLabel(System.Text.StringBuilder value, LineStop stop)
        {
            string name = (stop.Name ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace(',', '，')
                .Replace('>', '→')
                .Replace('[', '(')
                .Replace(']', ')');
            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, stop.StopKey, System.StringComparison.Ordinal))
            {
                value.Append(name);
                value.Append('(');
                value.Append(stop.StopKey);
                value.Append(')');
                return;
            }
            value.Append(stop.StopKey);
        }
        private LineStructurePlan BuildRailPlan(
            RailStructureState state,
            LineStopLayout newLayout)
        {
            LineTrackChain oldChain = state.StableChain;
            LineTrackChain newChain = state.CandidateChain;
            LineStopLayout oldLayout = state.StableLayout;
            string oldStopSig = oldLayout != null
                ? oldLayout.StopSig
                : state.PendingOldStopSig;
            string newStopSig = newLayout != null ? newLayout.StopSig : string.Empty;
            bool stopSigChanged;
            LineStructurePlanKind kind;
            LineIntervalImpact impact = null;
            if (state.Phase == RailPhase.MissingBaseline)
            {
                kind = LineStructurePlanKind.CrossSaveFallback;
                stopSigChanged = !string.Equals(
                    oldStopSig,
                    newStopSig,
                    System.StringComparison.Ordinal);
            }
            else if (oldChain == null || oldLayout == null || newChain == null || newLayout == null
                || oldChain.Mode != newChain.Mode
                || !IsSupportedRailMode(oldChain.Mode)
                || !IsSupportedRailMode(newChain.Mode))
            {
                kind = LineStructurePlanKind.SafetyFallback;
                stopSigChanged = !string.Equals(
                    oldStopSig,
                    newStopSig,
                    System.StringComparison.Ordinal);
            }
            else
            {
                System.Func<Entity, Entity, bool> equivalentTarget =
                    m_Runtime.m_TrackModel.CreateTargetComparer();
                impact = LineIntervalImpact.Compute(
                    oldLayout,
                    newLayout,
                    oldChain,
                    newChain,
                    equivalentTarget);
                stopSigChanged = impact.StopSigChanged;
                kind = impact.IsValid
                    ? LineStructurePlanKind.Precise
                    : LineStructurePlanKind.SafetyFallback;
            }
            if (kind == LineStructurePlanKind.SafetyFallback)
                impact = impact != null && impact.IsValid ? impact : null;
            int expectedOldAffected = kind == LineStructurePlanKind.Precise
                ? -1
                : IntervalCount(oldLayout);
            int expectedNewAffected = kind == LineStructurePlanKind.Precise
                ? -1
                : IntervalCount(newLayout);
            return new LineStructurePlan(
                state.Line,
                state.LineId,
                state.Mode,
                kind,
                state.CandidateRevision,
                oldChain,
                newChain,
                oldLayout,
                newLayout,
                impact,
                oldStopSig,
                newStopSig,
                stopSigChanged,
                DecideLineTimes(kind, oldLayout, newLayout, stopSigChanged),
                stopSigChanged ? TimetablePlanKind.Reevaluate : TimetablePlanKind.Preserve,
                CaptureLineVehicles(state.Line),
                expectedOldAffected,
                expectedNewAffected,
                kind == LineStructurePlanKind.Precise ? -1 : 0);
        }
        private LineStructurePlan BuildDeletedPlan(RailStructureState state)
        {
            LineStopLayout oldLayout = state.StableLayout;
            string oldStopSig = oldLayout != null
                ? oldLayout.StopSig
                : state.PendingOldStopSig;
            int oldIntervalCount = oldLayout != null && oldLayout.StopCount >= 2
                ? oldLayout.StopCount
                : 0;
            return new LineStructurePlan(
                state.Line,
                state.LineId,
                state.Mode,
                LineStructurePlanKind.LineDeleted,
                state.CandidateRevision,
                state.StableChain,
                null,
                oldLayout,
                null,
                null,
                oldStopSig,
                string.Empty,
                !string.IsNullOrEmpty(oldStopSig),
                LineTimesPlanKind.Remove,
                string.Equals(oldStopSig, string.Empty, System.StringComparison.Ordinal)
                    ? TimetablePlanKind.Preserve
                    : TimetablePlanKind.Reevaluate,
                CaptureLineVehicles(state.Line),
                oldIntervalCount,
                0,
                0);
        }
        private static LineTimesPlanKind DecideLineTimes(
            LineStructurePlanKind kind,
            LineStopLayout oldLayout,
            LineStopLayout newLayout,
            bool stopSigChanged)
        {
            if (kind != LineStructurePlanKind.Precise
                || oldLayout == null
                || newLayout == null)
            {
                return LineTimesPlanKind.Remove;
            }
            if (newLayout.StopCount < 2)
                return LineTimesPlanKind.Remove;
            if (stopSigChanged
                || oldLayout.WaypointCount != newLayout.WaypointCount
                || oldLayout.StopCount != newLayout.StopCount)
            {
                return LineTimesPlanKind.Rebuild;
            }
            for (int i = 0; i < oldLayout.StopCount; i++)
            {
                if (oldLayout[i].WaypointIndex != newLayout[i].WaypointIndex)
                    return LineTimesPlanKind.Rebuild;
            }
            return LineTimesPlanKind.Refresh;
        }
        private static int IntervalCount(LineStopLayout layout)
        {
            return layout != null && layout.StopCount >= 2 ? layout.StopCount : 0;
        }
        private static bool IsSupportedRailMode(TransitMode mode)
        {
            TransportModeProfile profile = TransportModeProfile.GetProfile(mode);
            return profile.IsSupported && profile.Lifecycle == LifecycleKind.Rail;
        }
        private Entity[] CaptureLineVehicles(Entity line)
        {
            if (line == Entity.Null || m_Runtime.m_VehicleView == null)
                return System.Array.Empty<Entity>();
            List<Entity> matches = new List<Entity>();
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        && vehicleLine == line)
                    {
                        matches.Add(vehicle);
                    }
                }
            }
            finally
            {
                vehicles.Dispose();
            }
            return matches.Count == 0 ? System.Array.Empty<Entity>() : matches.ToArray();
        }
        private void DrainRoadLine(PendingRoadInvalidation pending)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            if (pending.NextRetryFrame != 0 && frame < pending.NextRetryFrame)
            {
                QueueRetry(pending);
                return;
            }
            if (!m_Runtime.EntityManager.Exists(line))
            {
                ClearUnavailableRoad(pending, "line-deleted");
                return;
            }
            bool hasLayout = m_Runtime.m_LineView.TryStopLayout(
                line,
                out string stopSig,
                out int[] waypointIndices);
            if (!hasLayout || string.IsNullOrEmpty(stopSig))
            {
                string reason = LayoutTerminalReason(line, pending.RetryCount);
                if (!string.IsNullOrEmpty(reason))
                    ClearUnavailableRoad(pending, reason);
                else
                    QueueRetry(pending.WithRetry(m_Runtime.m_SimulationSystem.frameIndex));
                return;
            }
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                stopSig,
                "stop-sig-changed",
                clearDetails: false,
                publishEvent: true);
            RapidTransitMod.PassengerFlow.Runtime.Current?.InvalidateAnchors(line);
            m_Runtime.m_Observation.InvalidateBusRoute(line, pending.OldRoute, pending.NewRoute);
            m_Runtime.m_LineTimes.InvalidateLine(line);
            if (RoadEntryChanged(pending.OldRoute, pending.NewRoute))
                m_Runtime.m_Observation.InvalidateDispatchTiming(line);
            m_Runtime.m_RoadEventSource.InvalidateLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line)
                    {
                        continue;
                    }
                    m_Runtime.m_RoadEventSource.CommitWaypoint(vehicle, -1);
                    m_Runtime.m_RouteProgress.Remove(vehicle);
                    m_Runtime.m_StopRuntime.ReprojectTimedPlan(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
                    m_Runtime.m_Observation.SuppressMonitor(
                        vehicle,
                        stopSig,
                        waypointIndices,
                        m_Runtime.m_SimulationSystem.frameIndex);
                    m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
                    RapidTransitMod.PassengerFlow.Runtime.Current?.RemoveVehicle(vehicle);
                    m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Stop);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }
        private static bool RoadEntryChanged(
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            Entity oldWaypoint = oldRoute != null && oldRoute.Waypoints.Length > 0
                ? oldRoute.Waypoints[0]
                : Entity.Null;
            Entity newWaypoint = newRoute != null && newRoute.Waypoints.Length > 0
                ? newRoute.Waypoints[0]
                : Entity.Null;
            if (oldWaypoint != newWaypoint)
                return true;
            Entity oldStop = FirstResolvedStop(oldRoute);
            Entity newStop = FirstResolvedStop(newRoute);
            return oldStop != newStop;
        }
        private static Entity FirstResolvedStop(LineProfile.RoadRouteSnapshot route)
        {
            if (route == null)
                return Entity.Null;
            for (int i = 0; i < route.Stops.Length; i++)
            {
                if (route.Stops[i] != Entity.Null)
                    return route.Stops[i];
            }
            return Entity.Null;
        }
        private void QueueRetry(PendingRoadInvalidation pending)
        {
            m_PendingRoadLines[pending.Line] = pending;
            TrackRetryFrame(pending.NextRetryFrame);
        }
        private void TrackRetryFrame(uint frame)
        {
            if (frame == 0)
                return;
            if (m_NextLayoutRetryFrame == 0 || frame < m_NextLayoutRetryFrame)
                m_NextLayoutRetryFrame = frame;
        }
        private string LayoutTerminalReason(Entity line, byte retryCount)
        {
            if (!m_Runtime.EntityManager.Exists(line))
                return "line-deleted";
            if (!m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return retryCount >= LayoutRetryLimit ? "route-buffer-unavailable" : string.Empty;
            DynamicBuffer<RouteWaypoint> waypoints =
                m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0)
                return "route-waypoints-empty";
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypoint)
                    && m_Runtime.m_Resolve.Stop(waypoint) != Entity.Null)
                {
                    return retryCount >= LayoutRetryLimit ? "layout-retry-exhausted" : string.Empty;
                }
            }
            return retryCount >= LayoutRetryLimit
                ? "route-has-no-valid-stop"
                : string.Empty;
        }
        private void ClearUnavailableRoad(PendingRoadInvalidation pending, string reason)
        {
            Entity line = pending.Line;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_WorkbenchBridge.OnAuthoritativeLineInvalidated(
                line,
                pending.LineId,
                pending.Mode,
                string.Empty,
                NoticeTrigger(reason),
                clearDetails: true,
                publishEvent: false);
            ReleaseTimedPlans(line);
            m_Runtime.m_Observation.ReleaseLineMonitor(line, frame);
            RapidTransitMod.PassengerFlow.Runtime.Current?.InvalidateAnchors(line);
            m_Runtime.m_Observation.InvalidateBusRoute(line, pending.OldRoute, pending.NewRoute);
            m_Runtime.m_LineTimes.InvalidateLine(line);
            m_Runtime.m_RoadEventSource.InvalidateLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line)
                    {
                        continue;
                    }
                    m_Runtime.m_RoadEventSource.CommitWaypoint(vehicle, -1);
                    m_Runtime.m_RouteProgress.Remove(vehicle);
                    m_Runtime.m_StopRuntime.InvalidateVehiclePosition(vehicle);
                    RapidTransitMod.PassengerFlow.Runtime.Current?.RemoveVehicle(vehicle);
                    m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Stop);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
            m_Runtime.log.Info("[LineStructureInvalidated] line=" + line.Index
                + " layout=unavailable road=1 reason=" + reason
                + " retries=" + pending.RetryCount);
            PushReleaseNotice(pending.LineId, pending.Mode, reason, pending.RetryCount);
        }
        private void ReleaseTimedPlans(Entity line)
        {
            List<Entity> vehicles = new List<Entity>();
            foreach (TimedPlanSnapshot snapshot in m_Runtime.m_StopRuntime.TimedPlans())
                if (snapshot.Line == line)
                    vehicles.Add(snapshot.Vehicle);
            for (int i = 0; i < vehicles.Count; i++)
                m_Runtime.m_StopRuntime.ClearTimedPlan(vehicles[i]);
        }
        private void PushReleaseNotice(
            string lineId,
            string mode,
            string reason,
            byte retryCount)
        {
            if (string.IsNullOrEmpty(lineId))
                return;
            DispatchWorkbenchLineInvalidationEvent payload = new DispatchWorkbenchLineInvalidationEvent
            {
                mode = mode ?? string.Empty,
                version = m_Runtime.m_WorkbenchBridge.Version.ToString(),
                lineIds = new[] { lineId },
                reasons = new[]
                {
                    new DispatchWorkbenchCleanupReasonDto
                    {
                        lineId = lineId,
                        reason = "backend-applied-cleared;default-restored;trigger="
                            + NoticeTrigger(reason)
                            + ";detail="
                            + reason
                            + ";retries="
                            + retryCount
                    }
                }
            };
            // 该通知只用于后端实际释放后的前端状态同步，避免前端继续显示已应用。
            Workbenches.UiEvents.Push(payload);
        }
        private static string NoticeTrigger(string reason)
        {
            if (string.Equals(reason, "line-deleted", System.StringComparison.Ordinal))
                return "line-deleted";
            if (string.Equals(reason, "route-waypoints-empty", System.StringComparison.Ordinal)
                || string.Equals(reason, "route-has-no-valid-stop", System.StringComparison.Ordinal))
            {
                return "no-valid-stop";
            }
            return "continuous-validation-failed";
        }
    }
}
