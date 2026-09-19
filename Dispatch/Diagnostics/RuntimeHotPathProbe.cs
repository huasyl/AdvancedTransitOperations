using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RapidTransitMod.Runtime;
using RapidTransitMod.TrackProjection;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal enum RuntimeCostPhase
    {
        RailEta,
        Setup,
        SourceCollect,
        Register,
        SourceRoute,
        Stop,
        Rescue,
        BypassDecision,
        DwellDeparture,
        Dispatch,
        Scheduler,
        RetireSlice,
        Notices,
        Events,
        Announcements,
        VehicleCache,
        CatalogCheck,
        CatalogTick
    }

    internal struct RuntimeCostFrame
    {
        public bool Enabled;
        public uint Frame;
        public long Started;
        public long Checkpoint;
        public long RailEta;
        public long Setup;
        public long Source;
        public long SourceCollect;
        public long Register;
        public long SourceRoute;
        public long Stop;
        public long Bypass;
        public long Rescue;
        public long BypassDecision;
        public long DwellDeparture;
        public long Dispatch;
        public long Scheduler;
        public long Finalize;
        public long RetireSlice;
        public long Notices;
        public long Events;
        public long Announcements;
        public long Maintenance;
        public long VehicleCache;
        public long CatalogCheck;
        public long CatalogTick;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    internal struct RuntimeCostContext
    {
        public bool SourceFrame;
        public bool FullMinuteSweep;
        public int Stop;
        public int Rescue;
        public int Bypass;
        public int Dispatch;
        public int Retire;
        public int Slice;
        public int DirtyLines;
    }

    internal struct RuntimeSlowFrame
    {
        public long Total;
        public RuntimeCostFrame Frame;
        public RuntimeCostContext Context;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    internal sealed class RuntimeHotPathProbe
    {
        private const uint FlushIntervalFrames = 512;
        private const int CostTopPerSecond = 5;
        private const int CostSecondCapacity = 600;
        private static readonly long s_SecondTicks = Stopwatch.Frequency;
        private static readonly long s_SlowFrameTicks = Stopwatch.Frequency / 250L;
        private static readonly long s_SevereFrameTicks = Stopwatch.Frequency / 80L;
        private readonly TimedLogger m_Log;
        private uint m_LastFlushFrame;
        private ulong m_SourceRows;
        private ulong m_FrameRows;
        private ulong m_EnsureFrameRows;
        private ulong m_OfficialBoardingChanged;
        private ulong m_MovingChanged;
        private ulong m_DeparturePendingDemand;
        private ulong m_BypassWatchDemand;
        private ulong m_BypassActiveDemand;
        private ulong m_OriginCandidateDemand;
        private ulong m_InboundWatchDemand;
        private ulong m_HeavyDetailReads;
        private ulong m_PathDetailReads;
        private ulong m_NavigationDetailReads;
        private ulong m_BusinessFacts;
        private ulong m_DueDeadlines;
        private ulong m_StopStagePlans;
        private ulong m_BypassStagePlans;
        private ulong m_DispatchStagePlans;
        private ulong m_RetireStagePlans;
        private ulong m_RescueStagePlans;
        private ulong m_SliceStagePlans;
        private ulong m_StopStageExecuted;
        private ulong m_BypassStageExecuted;
        private ulong m_DispatchStageExecuted;
        private ulong m_RetireStageExecuted;
        private ulong m_RescueStageExecuted;
        private ulong m_SliceStageExecuted;
        private ulong m_SchedulerExternalDirtyLines;
        private ulong m_ProjectionCacheRequests;
        private ulong m_ProjectionCacheSuccessHits;
        private ulong m_ProjectionCacheFailureHits;
        private readonly ulong[] m_ProjectionRequestsBySource = new ulong[7];
        private readonly ulong[] m_ProjectionCacheHitsBySource = new ulong[7];
        private readonly ulong[] m_ProjectionAvailableBySource = new ulong[7];
        private readonly ulong[] m_ProjectionUnavailableBySource = new ulong[7];
        private readonly ulong[] m_ProjectionExactAvailableBySource = new ulong[7];
        private readonly ulong[] m_ProjectionFallbackAvailableBySource = new ulong[7];
        private readonly ulong[] m_ProjectionStrictFailedBySource = new ulong[7];
        private readonly ulong[] m_ProjectionOrdinaryFailedBySource = new ulong[7];
        private readonly ulong[] m_ProjectionLineSnapshotHitsBySource = new ulong[7];
        private ulong m_ProjectionCalculations;
        private ulong m_ProjectionExactInitial;
        private ulong m_ProjectionExactDirection;
        private ulong m_ProjectionExactNavigation;
        private ulong m_ProjectionExactPathTail;
        private ulong m_ProjectionExactIndependentBoarding;
        private ulong m_ProjectionExactDepartureSession;
        private ulong m_ProjectionExactArrivalTarget;
        private ulong m_ProjectionCurrentLaneUnavailable;
        private ulong m_ProjectionNoCandidates;
        private ulong m_ProjectionAllExcluded;
        private ulong m_ProjectionAmbiguous;
        private ulong m_ProjectionZeroParameterSpan;
        private ulong m_ProjectionProgressUnavailable;
        private ulong m_ProjectionLaneNotInModel;
        private ulong m_ProjectionParameterOutsideRange;
        private ulong m_ProjectionInvalidParameter;
        private ulong m_ProjectionIndexedLaneMismatch;
        private ulong m_ProjectionPreparingLaneNotInModel;
        private ulong m_ProjectionFinalExact;
        private ulong m_ProjectionFinalRouteProgress;
        private ulong m_ProjectionFinalCachedWaypoint;
        private ulong m_ProjectionFinalAnchoredRouteProgress;
        private ulong m_ProjectionFinalFailures;
        private ulong m_WaypointStationConfirmed;
        private ulong m_WaypointStationRejected;
        private ulong m_ProjectionCurrentLaneReads;
        private ulong m_ProjectionNavigationReads;
        private ulong m_ProjectionPathReads;
        private long m_ProjectionCurrentLaneReadTicks;
        private long m_ProjectionNavigationReadTicks;
        private long m_ProjectionPathReadTicks;
        private long m_ProjectionCurrentLaneReadMaxTicks;
        private long m_ProjectionNavigationReadMaxTicks;
        private long m_ProjectionPathReadMaxTicks;
        private long m_ProjectionMatcherTicks;
        private long m_ProjectionMatcherMaxTicks;
        private long m_CostWindowStart;
        private long m_CostFrames;
        private long m_CostSlowFrames;
        private long m_CostSevereFrames;
        private long m_CostTotal;
        private long m_CostRailEta;
        private long m_CostSetup;
        private long m_CostSource;
        private long m_CostSourceCollect;
        private long m_CostRegister;
        private long m_CostSourceRoute;
        private long m_CostStop;
        private long m_CostBypass;
        private long m_CostRescue;
        private long m_CostBypassDecision;
        private long m_CostDwellDeparture;
        private long m_CostDispatch;
        private long m_CostScheduler;
        private long m_CostFinalize;
        private long m_CostRetireSlice;
        private long m_CostNotices;
        private long m_CostEvents;
        private long m_CostAnnouncements;
        private long m_CostMaintenance;
        private long m_CostVehicleCache;
        private long m_CostCatalogCheck;
        private long m_CostCatalogTick;
        private long m_CostGc0;
        private long m_CostGc1;
        private long m_CostGc2;
        private long m_CostMax;
        private RuntimeCostFrame m_CostMaxFrame;
        private RuntimeCostContext m_CostMaxContext;
        private uint m_CostStartFrame;
        private uint m_CostDurationFrames;
        private bool m_CostStarted;
        private bool m_CostCompleted;
        private int m_CostLastSecond;
        private readonly RuntimeSlowFrame[] m_CostSecondFrames = new RuntimeSlowFrame[CostSecondCapacity * CostTopPerSecond];
        private readonly int[] m_CostSecondCounts = new int[CostSecondCapacity];
        private readonly int[] m_CostSecondOffsets = new int[CostSecondCapacity];

        internal RuntimeHotPathProbe(TimedLogger log)
        {
            m_Log = log;
            ResetSecondOffsets();
        }

        internal static bool Enabled() => RtLog.VerboseEnabled;
        internal static bool CostEnabled() => BuildFlavor.PerfLogs || RtLog.VerboseEnabled;

        internal RuntimeCostFrame BeginCost(uint nowFrame, bool systemReady, uint durationFrames)
        {
            if (!CostEnabled() || !systemReady || m_CostCompleted)
                return default;

            long now = Stopwatch.GetTimestamp();
            if (!m_CostStarted)
            {
                m_CostStarted = true;
                m_CostStartFrame = nowFrame;
                m_CostDurationFrames = durationFrames;
                m_CostWindowStart = now;
                m_CostLastSecond = 0;
            }
            return new RuntimeCostFrame
            {
                Enabled = true,
                Frame = nowFrame,
                Started = now,
                Checkpoint = now,
                Gc0 = GC.CollectionCount(0),
                Gc1 = GC.CollectionCount(1),
                Gc2 = GC.CollectionCount(2)
            };
        }

        internal void MarkCost(ref RuntimeCostFrame frame, RuntimeCostPhase phase)
        {
            if (!frame.Enabled)
                return;

            long now = Stopwatch.GetTimestamp();
            long ticks = now - frame.Checkpoint;
            frame.Checkpoint = now;
            switch (phase)
            {
                case RuntimeCostPhase.RailEta: frame.RailEta += ticks; break;
                case RuntimeCostPhase.Setup: frame.Setup += ticks; break;
                case RuntimeCostPhase.SourceCollect: frame.Source += ticks; frame.SourceCollect += ticks; break;
                case RuntimeCostPhase.Register: frame.Source += ticks; frame.Register += ticks; break;
                case RuntimeCostPhase.SourceRoute: frame.Source += ticks; frame.SourceRoute += ticks; break;
                case RuntimeCostPhase.Stop: frame.Stop += ticks; break;
                case RuntimeCostPhase.Rescue: frame.Bypass += ticks; frame.Rescue += ticks; break;
                case RuntimeCostPhase.BypassDecision: frame.Bypass += ticks; frame.BypassDecision += ticks; break;
                case RuntimeCostPhase.DwellDeparture: frame.Bypass += ticks; frame.DwellDeparture += ticks; break;
                case RuntimeCostPhase.Dispatch: frame.Dispatch += ticks; break;
                case RuntimeCostPhase.Scheduler: frame.Scheduler += ticks; break;
                case RuntimeCostPhase.RetireSlice: frame.Finalize += ticks; frame.RetireSlice += ticks; break;
                case RuntimeCostPhase.Notices: frame.Finalize += ticks; frame.Notices += ticks; break;
                case RuntimeCostPhase.Events: frame.Finalize += ticks; frame.Events += ticks; break;
                case RuntimeCostPhase.Announcements: frame.Finalize += ticks; frame.Announcements += ticks; break;
                case RuntimeCostPhase.VehicleCache: frame.Maintenance += ticks; frame.VehicleCache += ticks; break;
                case RuntimeCostPhase.CatalogCheck: frame.Maintenance += ticks; frame.CatalogCheck += ticks; break;
                case RuntimeCostPhase.CatalogTick: frame.Maintenance += ticks; frame.CatalogTick += ticks; break;
            }
        }

        internal void FinishCost(ref RuntimeCostFrame frame, RuntimeCostContext context)
        {
            if (!frame.Enabled)
                return;

            long now = Stopwatch.GetTimestamp();
            frame.Maintenance += now - frame.Checkpoint;
            long total = now - frame.Started;
            int gc0 = Math.Max(0, GC.CollectionCount(0) - frame.Gc0);
            int gc1 = Math.Max(0, GC.CollectionCount(1) - frame.Gc1);
            int gc2 = Math.Max(0, GC.CollectionCount(2) - frame.Gc2);

            m_CostFrames++;
            m_CostTotal += total;
            m_CostRailEta += frame.RailEta;
            m_CostSetup += frame.Setup;
            m_CostSource += frame.Source;
            m_CostSourceCollect += frame.SourceCollect;
            m_CostRegister += frame.Register;
            m_CostSourceRoute += frame.SourceRoute;
            m_CostStop += frame.Stop;
            m_CostBypass += frame.Bypass;
            m_CostRescue += frame.Rescue;
            m_CostBypassDecision += frame.BypassDecision;
            m_CostDwellDeparture += frame.DwellDeparture;
            m_CostDispatch += frame.Dispatch;
            m_CostScheduler += frame.Scheduler;
            m_CostFinalize += frame.Finalize;
            m_CostRetireSlice += frame.RetireSlice;
            m_CostNotices += frame.Notices;
            m_CostEvents += frame.Events;
            m_CostAnnouncements += frame.Announcements;
            m_CostMaintenance += frame.Maintenance;
            m_CostVehicleCache += frame.VehicleCache;
            m_CostCatalogCheck += frame.CatalogCheck;
            m_CostCatalogTick += frame.CatalogTick;
            m_CostGc0 += gc0;
            m_CostGc1 += gc1;
            m_CostGc2 += gc2;
            if (total >= s_SlowFrameTicks) m_CostSlowFrames++;
            if (total >= s_SevereFrameTicks) m_CostSevereFrames++;
            if (total > m_CostMax)
            {
                m_CostMax = total;
                m_CostMaxFrame = frame;
                m_CostMaxContext = context;
            }

            if (total >= s_SlowFrameTicks)
                StoreSlowFrame(now, total, frame, context, gc0, gc1, gc2);

            if (unchecked(frame.Frame - m_CostStartFrame) >= m_CostDurationFrames)
            {
                FlushCosts(now);
                m_CostCompleted = true;
            }
        }

        internal void CountSourceRow()
        {
            if (Enabled()) m_SourceRows++;
        }

        internal void CountFrameRow()
        {
            if (Enabled()) m_FrameRows++;
        }

        internal void CountEnsureFrameRow()
        {
            if (Enabled()) m_EnsureFrameRows++;
        }

        internal void CountOfficialBoardingChanged()
        {
            if (Enabled()) m_OfficialBoardingChanged++;
        }

        internal void CountMovingChanged()
        {
            if (Enabled()) m_MovingChanged++;
        }

        internal void CountDemand(RuntimeDemandMask demand)
        {
            if (!Enabled())
                return;

            if ((demand & RuntimeDemandMask.DeparturePending) != 0) m_DeparturePendingDemand++;
            if ((demand & RuntimeDemandMask.BypassWatch) != 0) m_BypassWatchDemand++;
            if ((demand & RuntimeDemandMask.BypassActive) != 0) m_BypassActiveDemand++;
            if ((demand & RuntimeDemandMask.OriginCandidate) != 0) m_OriginCandidateDemand++;
            if ((demand & RuntimeDemandMask.InboundWatch) != 0) m_InboundWatchDemand++;
        }

        internal void CountPathDetailRead()
        {
            if (Enabled()) m_PathDetailReads++;
        }

        internal void CountHeavyDetailRead()
        {
            if (Enabled()) m_HeavyDetailReads++;
        }

        internal void CountNavigationDetailRead()
        {
            if (Enabled()) m_NavigationDetailReads++;
        }

        internal void CountBusinessFact()
        {
            if (Enabled()) m_BusinessFacts++;
        }

        internal void CountDueDeadlines(int count)
        {
            if (Enabled() && count > 0)
                m_DueDeadlines += (ulong)count;
        }

        internal void CountStagePlan(RuntimeStageMask stage, int count)
        {
            if (!Enabled() || count <= 0)
                return;

            switch (stage)
            {
                case RuntimeStageMask.Stop: m_StopStagePlans += (ulong)count; break;
                case RuntimeStageMask.Bypass: m_BypassStagePlans += (ulong)count; break;
                case RuntimeStageMask.Dispatch: m_DispatchStagePlans += (ulong)count; break;
                case RuntimeStageMask.Retire: m_RetireStagePlans += (ulong)count; break;
                case RuntimeStageMask.Rescue: m_RescueStagePlans += (ulong)count; break;
                case RuntimeStageMask.Slice: m_SliceStagePlans += (ulong)count; break;
            }
        }

        internal void CountStageExecuted(RuntimeStageMask stage, int count)
        {
            if (!Enabled() || count <= 0)
                return;

            switch (stage)
            {
                case RuntimeStageMask.Stop: m_StopStageExecuted += (ulong)count; break;
                case RuntimeStageMask.Bypass: m_BypassStageExecuted += (ulong)count; break;
                case RuntimeStageMask.Dispatch: m_DispatchStageExecuted += (ulong)count; break;
                case RuntimeStageMask.Retire: m_RetireStageExecuted += (ulong)count; break;
                case RuntimeStageMask.Rescue: m_RescueStageExecuted += (ulong)count; break;
                case RuntimeStageMask.Slice: m_SliceStageExecuted += (ulong)count; break;
            }
        }

        internal void CountSchedulerExternalDirty(int count)
        {
            if (Enabled() && count > 0)
                m_SchedulerExternalDirtyLines += (ulong)count;
        }

        internal void RecordProjectionCacheAccess(ProjectionRequestSource source, bool cacheHit, bool available, VehicleTrackCursorSource cursorSource, bool exactOnly)
        {
            if (!Enabled())
                return;

            m_ProjectionCacheRequests++;
            int sourceIndex = (int)source;
            if (sourceIndex >= 0 && sourceIndex < m_ProjectionRequestsBySource.Length)
            {
                m_ProjectionRequestsBySource[sourceIndex]++;
                if (cacheHit)
                    m_ProjectionCacheHitsBySource[sourceIndex]++;
                if (available)
                {
                    m_ProjectionAvailableBySource[sourceIndex]++;
                    if (cursorSource == VehicleTrackCursorSource.CurrentLane)
                        m_ProjectionExactAvailableBySource[sourceIndex]++;
                    else
                        m_ProjectionFallbackAvailableBySource[sourceIndex]++;
                }
                else
                {
                    m_ProjectionUnavailableBySource[sourceIndex]++;
                    if (exactOnly)
                        m_ProjectionStrictFailedBySource[sourceIndex]++;
                    else
                        m_ProjectionOrdinaryFailedBySource[sourceIndex]++;
                }
            }
            if (!cacheHit)
                return;

            if (available)
                m_ProjectionCacheSuccessHits++;
            else
                m_ProjectionCacheFailureHits++;
        }

        internal void RecordProjectionLineSnapshotAccess(ProjectionRequestSource source, bool cacheHit)
        {
            if (!Enabled() || !cacheHit)
                return;
            int sourceIndex = (int)source;
            if (sourceIndex >= 0 && sourceIndex < m_ProjectionLineSnapshotHitsBySource.Length)
                m_ProjectionLineSnapshotHitsBySource[sourceIndex]++;
        }

        internal void RecordProjectionRead(ProjectionReadKind kind, long ticks)
        {
            if (!Enabled())
                return;

            switch (kind)
            {
                case ProjectionReadKind.CurrentLane:
                    AddProjectionRead(
                        ref m_ProjectionCurrentLaneReads,
                        ref m_ProjectionCurrentLaneReadTicks,
                        ref m_ProjectionCurrentLaneReadMaxTicks,
                        ticks);
                    break;
                case ProjectionReadKind.Navigation:
                    AddProjectionRead(
                        ref m_ProjectionNavigationReads,
                        ref m_ProjectionNavigationReadTicks,
                        ref m_ProjectionNavigationReadMaxTicks,
                        ticks);
                    break;
                case ProjectionReadKind.Path:
                    AddProjectionRead(
                        ref m_ProjectionPathReads,
                        ref m_ProjectionPathReadTicks,
                        ref m_ProjectionPathReadMaxTicks,
                        ticks);
                    break;
            }
        }

        internal void RecordProjectionOutcome(ProjectionOutcome outcome)
        {
            if (!Enabled())
                return;

            if (!outcome.ExactCounted)
            {
                m_ProjectionCalculations++;
                AddProjectionMatcherTicks(outcome.MatcherTicks);
                CountProjectionExact(outcome);
            }
            CountProjectionFinal(outcome);
        }

        internal void RecordProjectionExactFailure(ProjectionOutcome outcome)
        {
            if (!Enabled())
                return;
            m_ProjectionCalculations++;
            AddProjectionMatcherTicks(outcome.MatcherTicks);
            CountProjectionExact(outcome);
        }

        internal void RecordWaypointStationOutcome(bool confirmed)
        {
            if (!Enabled())
                return;

            if (confirmed)
                m_WaypointStationConfirmed++;
            else
                m_WaypointStationRejected++;
        }

        private void CountProjectionExact(ProjectionOutcome outcome)
        {
            if (IsPreparingWaypointLaneNotInModel(outcome))
            {
                m_ProjectionPreparingLaneNotInModel++;
                return;
            }
            if (outcome.ExactFailure != ProjectionExactFailure.None)
            {
                switch (outcome.ExactFailure)
                {
                    case ProjectionExactFailure.CurrentLaneUnavailable: m_ProjectionCurrentLaneUnavailable++; break;
                    case ProjectionExactFailure.NoCandidates: m_ProjectionNoCandidates++; break;
                    case ProjectionExactFailure.AllExcluded: m_ProjectionAllExcluded++; break;
                    case ProjectionExactFailure.Ambiguous: m_ProjectionAmbiguous++; break;
                    case ProjectionExactFailure.ZeroParameterSpan: m_ProjectionZeroParameterSpan++; break;
                    case ProjectionExactFailure.ProgressUnavailable: m_ProjectionProgressUnavailable++; break;
                    case ProjectionExactFailure.LaneNotInModel: m_ProjectionLaneNotInModel++; break;
                    case ProjectionExactFailure.ParameterOutsideRange: m_ProjectionParameterOutsideRange++; break;
                    case ProjectionExactFailure.InvalidParameter: m_ProjectionInvalidParameter++; break;
                    case ProjectionExactFailure.IndexedLaneMismatch: m_ProjectionIndexedLaneMismatch++; break;
                }
                return;
            }

            switch (outcome.ExactBasis)
            {
                case ProjectionMatchBasis.Initial: m_ProjectionExactInitial++; break;
                case ProjectionMatchBasis.Direction: m_ProjectionExactDirection++; break;
                case ProjectionMatchBasis.Navigation: m_ProjectionExactNavigation++; break;
                case ProjectionMatchBasis.PathTail: m_ProjectionExactPathTail++; break;
                case ProjectionMatchBasis.IndependentBoarding: m_ProjectionExactIndependentBoarding++; break;
                case ProjectionMatchBasis.DepartureSession: m_ProjectionExactDepartureSession++; break;
                case ProjectionMatchBasis.ArrivalTarget: m_ProjectionExactArrivalTarget++; break;
            }
        }

        private void CountProjectionFinal(ProjectionOutcome outcome)
        {
            if (!outcome.FinalSuccess)
            {
                m_ProjectionFinalFailures++;
                return;
            }

            switch (outcome.FinalSource)
            {
                case VehicleTrackCursorSource.CurrentLane: m_ProjectionFinalExact++; break;
                case VehicleTrackCursorSource.RouteProgress: m_ProjectionFinalRouteProgress++; break;
                case VehicleTrackCursorSource.CachedWaypoint: m_ProjectionFinalCachedWaypoint++; break;
                case VehicleTrackCursorSource.AnchoredRouteProgress: m_ProjectionFinalAnchoredRouteProgress++; break;
                default: m_ProjectionFinalFailures++; break;
            }
        }

        private static bool IsPreparingWaypointLaneNotInModel(ProjectionOutcome outcome)
        {
            return outcome.RequestSource == ProjectionRequestSource.Waypoint
                && outcome.ExactFailure == ProjectionExactFailure.LaneNotInModel
                && outcome.RuntimeContext.VehicleStateKnown
                && outcome.RuntimeContext.VehicleState == VehicleState.Preparing
                && outcome.RuntimeContext.PublicTransportKnown
                && !outcome.RuntimeContext.OfficialBoarding;
        }

        private static void AddProjectionRead(
            ref ulong count,
            ref long totalTicks,
            ref long maxTicks,
            long ticks)
        {
            count++;
            totalTicks += ticks;
            if (ticks > maxTicks)
                maxTicks = ticks;
        }

        private void AddProjectionMatcherTicks(long ticks)
        {
            if (ticks <= 0)
                return;

            m_ProjectionMatcherTicks += ticks;
            if (ticks > m_ProjectionMatcherMaxTicks)
                m_ProjectionMatcherMaxTicks = ticks;
        }

        internal void FlushIfDue(uint nowFrame)
        {
            if (!Enabled())
                return;

            if (m_LastFlushFrame == 0)
            {
                m_LastFlushFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_LastFlushFrame;
            if (elapsedFrames < FlushIntervalFrames)
                return;

            if (HasCounts())
            {
                m_Log.Info("[RuntimeHotPathProbe] frames=" + elapsedFrames
                    + " sourceRows=" + m_SourceRows
                    + " frameRows=" + m_FrameRows
                    + " ensureFrameRow=" + m_EnsureFrameRows
                    + " changes=" + m_OfficialBoardingChanged + "/" + m_MovingChanged
                    + " demands=" + m_DeparturePendingDemand + "/" + m_BypassWatchDemand + "/" + m_BypassActiveDemand
                    + "/" + m_OriginCandidateDemand + "/" + m_InboundWatchDemand
                    + " detail=" + m_HeavyDetailReads + "/" + m_PathDetailReads + "/" + m_NavigationDetailReads
                    + " facts=" + m_BusinessFacts
                    + " due=" + m_DueDeadlines
                    + " stagePlan=" + m_StopStagePlans + "/" + m_RescueStagePlans + "/" + m_BypassStagePlans
                    + "/" + m_DispatchStagePlans + "/" + m_RetireStagePlans + "/" + m_SliceStagePlans
                    + " stageExec=" + m_StopStageExecuted + "/" + m_RescueStageExecuted + "/" + m_BypassStageExecuted
                    + "/" + m_DispatchStageExecuted + "/" + m_RetireStageExecuted + "/" + m_SliceStageExecuted
                    + " schedulerExternalDirty=" + m_SchedulerExternalDirtyLines);
            }

            FlushProjectionDiagnostics(elapsedFrames);

            ClearCounts();
            m_LastFlushFrame = nowFrame;
        }

        internal void Clear()
        {
            ClearCounts();
            ClearCosts();
        }

        private void ClearCounts()
        {
            m_LastFlushFrame = 0;
            m_SourceRows = 0;
            m_FrameRows = 0;
            m_EnsureFrameRows = 0;
            m_OfficialBoardingChanged = 0;
            m_MovingChanged = 0;
            m_DeparturePendingDemand = 0;
            m_BypassWatchDemand = 0;
            m_BypassActiveDemand = 0;
            m_OriginCandidateDemand = 0;
            m_InboundWatchDemand = 0;
            m_HeavyDetailReads = 0;
            m_PathDetailReads = 0;
            m_NavigationDetailReads = 0;
            m_BusinessFacts = 0;
            m_DueDeadlines = 0;
            m_StopStagePlans = 0;
            m_BypassStagePlans = 0;
            m_DispatchStagePlans = 0;
            m_RetireStagePlans = 0;
            m_RescueStagePlans = 0;
            m_SliceStagePlans = 0;
            m_StopStageExecuted = 0;
            m_BypassStageExecuted = 0;
            m_DispatchStageExecuted = 0;
            m_RetireStageExecuted = 0;
            m_RescueStageExecuted = 0;
            m_SliceStageExecuted = 0;
            m_SchedulerExternalDirtyLines = 0;
            m_ProjectionCacheRequests = 0;
            m_ProjectionCacheSuccessHits = 0;
            m_ProjectionCacheFailureHits = 0;
            Array.Clear(m_ProjectionRequestsBySource, 0, m_ProjectionRequestsBySource.Length);
            Array.Clear(m_ProjectionCacheHitsBySource, 0, m_ProjectionCacheHitsBySource.Length);
            Array.Clear(m_ProjectionAvailableBySource, 0, m_ProjectionAvailableBySource.Length);
            Array.Clear(m_ProjectionUnavailableBySource, 0, m_ProjectionUnavailableBySource.Length);
            Array.Clear(m_ProjectionExactAvailableBySource, 0, m_ProjectionExactAvailableBySource.Length);
            Array.Clear(m_ProjectionFallbackAvailableBySource, 0, m_ProjectionFallbackAvailableBySource.Length);
            Array.Clear(m_ProjectionStrictFailedBySource, 0, m_ProjectionStrictFailedBySource.Length);
            Array.Clear(m_ProjectionOrdinaryFailedBySource, 0, m_ProjectionOrdinaryFailedBySource.Length);
            Array.Clear(m_ProjectionLineSnapshotHitsBySource, 0, m_ProjectionLineSnapshotHitsBySource.Length);
            m_ProjectionCalculations = 0;
            m_ProjectionExactInitial = 0;
            m_ProjectionExactDirection = 0;
            m_ProjectionExactNavigation = 0;
            m_ProjectionExactPathTail = 0;
            m_ProjectionExactIndependentBoarding = 0;
            m_ProjectionExactDepartureSession = 0;
            m_ProjectionExactArrivalTarget = 0;
            m_ProjectionCurrentLaneUnavailable = 0;
            m_ProjectionNoCandidates = 0;
            m_ProjectionAllExcluded = 0;
            m_ProjectionAmbiguous = 0;
            m_ProjectionZeroParameterSpan = 0;
            m_ProjectionProgressUnavailable = 0;
            m_ProjectionLaneNotInModel = 0;
            m_ProjectionParameterOutsideRange = 0;
            m_ProjectionInvalidParameter = 0;
            m_ProjectionIndexedLaneMismatch = 0;
            m_ProjectionPreparingLaneNotInModel = 0;
            m_ProjectionFinalExact = 0;
            m_ProjectionFinalRouteProgress = 0;
            m_ProjectionFinalCachedWaypoint = 0;
            m_ProjectionFinalAnchoredRouteProgress = 0;
            m_ProjectionFinalFailures = 0;
            m_WaypointStationConfirmed = 0;
            m_WaypointStationRejected = 0;
            m_ProjectionCurrentLaneReads = 0;
            m_ProjectionNavigationReads = 0;
            m_ProjectionPathReads = 0;
            m_ProjectionCurrentLaneReadTicks = 0;
            m_ProjectionNavigationReadTicks = 0;
            m_ProjectionPathReadTicks = 0;
            m_ProjectionCurrentLaneReadMaxTicks = 0;
            m_ProjectionNavigationReadMaxTicks = 0;
            m_ProjectionPathReadMaxTicks = 0;
            m_ProjectionMatcherTicks = 0;
            m_ProjectionMatcherMaxTicks = 0;
        }

        private void FlushProjectionDiagnostics(uint elapsedFrames)
        {
            if (!HasProjectionCounts())
                return;

            double tickMs = 1000d / Stopwatch.Frequency;
            m_Log.Info("[TrackProjectionProbe] frames=" + elapsedFrames
                + " cache=request/hitOk/hitFail/calc:"
                + m_ProjectionCacheRequests + "/" + m_ProjectionCacheSuccessHits + "/"
                + m_ProjectionCacheFailureHits + "/" + m_ProjectionCalculations
                + " exact=initial/direction/navigation/path/independentBoarding/departureSession/arrivalTarget:"
                + m_ProjectionExactInitial + "/" + m_ProjectionExactDirection + "/"
                + m_ProjectionExactNavigation + "/" + m_ProjectionExactPathTail + "/"
                + m_ProjectionExactIndependentBoarding + "/" + m_ProjectionExactDepartureSession + "/"
                + m_ProjectionExactArrivalTarget
                + " exactFail=current/noCandidate/allExcluded/ambiguous/zeroSpan/progress/laneMissing/paramOutside/invalidParam/indexMismatch:"
                + m_ProjectionCurrentLaneUnavailable + "/" + m_ProjectionNoCandidates + "/"
                + m_ProjectionAllExcluded + "/" + m_ProjectionAmbiguous + "/"
                + m_ProjectionZeroParameterSpan + "/" + m_ProjectionProgressUnavailable + "/"
                + m_ProjectionLaneNotInModel + "/" + m_ProjectionParameterOutsideRange + "/"
                + m_ProjectionInvalidParameter + "/" + m_ProjectionIndexedLaneMismatch
                + " preparingLaneMissing=" + m_ProjectionPreparingLaneNotInModel
                + " final=exact/route/cached/anchored/fail:"
                + m_ProjectionFinalExact + "/" + m_ProjectionFinalRouteProgress + "/"
                + m_ProjectionFinalCachedWaypoint + "/" + m_ProjectionFinalAnchoredRouteProgress + "/"
                + m_ProjectionFinalFailures
                + " readOpsMs=current[count/total/max]:"
                + FormatProjectionRead(m_ProjectionCurrentLaneReads, m_ProjectionCurrentLaneReadTicks, m_ProjectionCurrentLaneReadMaxTicks, tickMs)
                + " navigation:" + FormatProjectionRead(m_ProjectionNavigationReads, m_ProjectionNavigationReadTicks, m_ProjectionNavigationReadMaxTicks, tickMs)
                + " path:" + FormatProjectionRead(m_ProjectionPathReads, m_ProjectionPathReadTicks, m_ProjectionPathReadMaxTicks, tickMs)
                + " match[total/max]:" + FormatProjectionTicks(m_ProjectionMatcherTicks, tickMs)
                + "/" + FormatProjectionTicks(m_ProjectionMatcherMaxTicks, tickMs));

            for (int source = 1; source < m_ProjectionRequestsBySource.Length; source++)
            {
                if (m_ProjectionRequestsBySource[source] == 0 && m_ProjectionLineSnapshotHitsBySource[source] == 0)
                    continue;
                m_Log.Info("[TrackProjectionProbeSource] source=" + ((ProjectionRequestSource)source)
                    + " request/hit/exact/fallback/strictFail/ordinaryFail/lineSnapshotHit="
                    + m_ProjectionRequestsBySource[source] + "/" + m_ProjectionCacheHitsBySource[source]
                    + "/" + m_ProjectionExactAvailableBySource[source] + "/" + m_ProjectionFallbackAvailableBySource[source]
                    + "/" + m_ProjectionStrictFailedBySource[source] + "/" + m_ProjectionOrdinaryFailedBySource[source]
                    + "/" + m_ProjectionLineSnapshotHitsBySource[source]);
            }

            if (m_WaypointStationConfirmed > 0 || m_WaypointStationRejected > 0)
            {
                m_Log.Info("[WaypointStationProbe] frames=" + elapsedFrames
                    + " confirmed/rejected=" + m_WaypointStationConfirmed + "/" + m_WaypointStationRejected);
            }
        }

        private static string FormatProjectionRead(ulong count, long totalTicks, long maxTicks, double tickMs)
        {
            return count + "/" + FormatProjectionTicks(totalTicks, tickMs)
                + "/" + FormatProjectionTicks(maxTicks, tickMs);
        }

        private static string FormatProjectionTicks(long ticks, double tickMs)
        {
            return (ticks * tickMs).ToString("F3", CultureInfo.InvariantCulture);
        }

        private void StoreSlowFrame(
            long now,
            long total,
            RuntimeCostFrame frame,
            RuntimeCostContext context,
            int gc0,
            int gc1,
            int gc2)
        {
            int second = (int)Math.Min(int.MaxValue, Math.Max(0L, (now - m_CostWindowStart) / s_SecondTicks));
            m_CostLastSecond = Math.Max(m_CostLastSecond, second);
            int bucket = second % CostSecondCapacity;
            int offset = bucket * CostTopPerSecond;
            if (m_CostSecondOffsets[bucket] != second)
            {
                m_CostSecondOffsets[bucket] = second;
                m_CostSecondCounts[bucket] = 0;
                for (int i = 0; i < CostTopPerSecond; i++)
                    m_CostSecondFrames[offset + i] = default;
            }

            RuntimeSlowFrame sample = new RuntimeSlowFrame
            {
                Total = total,
                Frame = frame,
                Context = context,
                Gc0 = gc0,
                Gc1 = gc1,
                Gc2 = gc2
            };
            int count = m_CostSecondCounts[bucket];
            if (count == CostTopPerSecond
                && total <= m_CostSecondFrames[offset + CostTopPerSecond - 1].Total)
            {
                return;
            }

            int insert = Math.Min(count, CostTopPerSecond - 1);
            while (insert > 0 && total > m_CostSecondFrames[offset + insert - 1].Total)
            {
                m_CostSecondFrames[offset + insert] = m_CostSecondFrames[offset + insert - 1];
                insert--;
            }
            m_CostSecondFrames[offset + insert] = sample;
            if (count < CostTopPerSecond)
                m_CostSecondCounts[bucket] = count + 1;
        }

        private void FlushCosts(long now)
        {
            if (m_CostFrames <= 0)
                return;

            double tickMs = 1000d / Stopwatch.Frequency;
            double divisor = m_CostFrames;
            m_Log.Info("[RuntimeCostProbe] durationGameMinutes=30 windowMs=" + ((now - m_CostWindowStart) * tickMs).ToString("F0", CultureInfo.InvariantCulture)
                + " frames=" + m_CostFrames
                + " slow4ms=" + m_CostSlowFrames
                + " severe12ms=" + m_CostSevereFrames
                + " avgMs=" + (m_CostTotal * tickMs / divisor).ToString("F2", CultureInfo.InvariantCulture)
                + " maxMs=" + (m_CostMax * tickMs).ToString("F2", CultureInfo.InvariantCulture)
                + " phaseAvg=railEta/setup/source/stop/bypass/dispatch/scheduler/finalize/maintenance:"
                + FormatAverage(m_CostRailEta, tickMs, divisor)
                + "/" + FormatAverage(m_CostSetup, tickMs, divisor)
                + "/" + FormatAverage(m_CostSource, tickMs, divisor)
                + "/" + FormatAverage(m_CostStop, tickMs, divisor)
                + "/" + FormatAverage(m_CostBypass, tickMs, divisor)
                + "/" + FormatAverage(m_CostDispatch, tickMs, divisor)
                + "/" + FormatAverage(m_CostScheduler, tickMs, divisor)
                + "/" + FormatAverage(m_CostFinalize, tickMs, divisor)
                + "/" + FormatAverage(m_CostMaintenance, tickMs, divisor)
                + " detailAvg=sourceCollect/register/sourceRoute:"
                + FormatAverage(m_CostSourceCollect, tickMs, divisor)
                + "/" + FormatAverage(m_CostRegister, tickMs, divisor)
                + "/" + FormatAverage(m_CostSourceRoute, tickMs, divisor)
                + "|rescue/bypassDecision/dwellDeparture:"
                + FormatAverage(m_CostRescue, tickMs, divisor)
                + "/" + FormatAverage(m_CostBypassDecision, tickMs, divisor)
                + "/" + FormatAverage(m_CostDwellDeparture, tickMs, divisor)
                + "|retireSlice/notices/events/announcements:"
                + FormatAverage(m_CostRetireSlice, tickMs, divisor)
                + "/" + FormatAverage(m_CostNotices, tickMs, divisor)
                + "/" + FormatAverage(m_CostEvents, tickMs, divisor)
                + "/" + FormatAverage(m_CostAnnouncements, tickMs, divisor)
                + "|vehicleCache/catalogCheck/catalogTick:"
                + FormatAverage(m_CostVehicleCache, tickMs, divisor)
                + "/" + FormatAverage(m_CostCatalogCheck, tickMs, divisor)
                + "/" + FormatAverage(m_CostCatalogTick, tickMs, divisor)
                + " gc=" + m_CostGc0 + "/" + m_CostGc1 + "/" + m_CostGc2
                + " max=" + FormatSlowFrame(new RuntimeSlowFrame
                {
                    Total = m_CostMax,
                    Frame = m_CostMaxFrame,
                    Context = m_CostMaxContext
                }, tickMs));

            int firstSecond = Math.Max(0, m_CostLastSecond - CostSecondCapacity + 1);
            for (int second = firstSecond; second <= m_CostLastSecond; second++)
            {
                int bucket = second % CostSecondCapacity;
                if (m_CostSecondOffsets[bucket] != second || m_CostSecondCounts[bucket] == 0)
                    continue;

                int offset = bucket * CostTopPerSecond;
                var line = new StringBuilder(768);
                line.Append("[RuntimeCostProbeFrames] second=").Append(second)
                    .Append(" count=").Append(m_CostSecondCounts[bucket]);
                for (int i = 0; i < m_CostSecondCounts[bucket]; i++)
                {
                    line.Append(" f").Append(i).Append('=')
                        .Append(FormatSlowFrame(m_CostSecondFrames[offset + i], tickMs));
                }
                m_Log.Info(line.ToString());
            }
        }

        private static string FormatSlowFrame(RuntimeSlowFrame sample, double tickMs)
        {
            RuntimeCostFrame frame = sample.Frame;
            RuntimeCostContext context = sample.Context;
            return "frame:" + frame.Frame
                + ",total:" + (sample.Total * tickMs).ToString("F2", CultureInfo.InvariantCulture)
                + ",phase:" + FormatTicks(frame.RailEta, tickMs) + "/" + FormatTicks(frame.Setup, tickMs)
                + "/" + FormatTicks(frame.Source, tickMs) + "/" + FormatTicks(frame.Stop, tickMs)
                + "/" + FormatTicks(frame.Bypass, tickMs) + "/" + FormatTicks(frame.Dispatch, tickMs)
                + "/" + FormatTicks(frame.Scheduler, tickMs) + "/" + FormatTicks(frame.Finalize, tickMs)
                + "/" + FormatTicks(frame.Maintenance, tickMs)
                + ",detail:" + FormatTicks(frame.SourceCollect, tickMs) + "/" + FormatTicks(frame.Register, tickMs)
                + "/" + FormatTicks(frame.SourceRoute, tickMs) + "/" + FormatTicks(frame.Rescue, tickMs)
                + "/" + FormatTicks(frame.BypassDecision, tickMs) + "/" + FormatTicks(frame.DwellDeparture, tickMs)
                + "/" + FormatTicks(frame.RetireSlice, tickMs) + "/" + FormatTicks(frame.Notices, tickMs)
                + "/" + FormatTicks(frame.Events, tickMs) + "/" + FormatTicks(frame.Announcements, tickMs)
                + "/" + FormatTicks(frame.VehicleCache, tickMs) + "/" + FormatTicks(frame.CatalogCheck, tickMs)
                + "/" + FormatTicks(frame.CatalogTick, tickMs)
                + ",source:" + (context.SourceFrame ? 1 : 0)
                + ",minute:" + (context.FullMinuteSweep ? 1 : 0)
                + ",stage:" + context.Stop + "/" + context.Rescue + "/" + context.Bypass
                + "/" + context.Dispatch + "/" + context.Retire + "/" + context.Slice
                + ",dirty:" + context.DirtyLines
                + ",gc:" + sample.Gc0 + "/" + sample.Gc1 + "/" + sample.Gc2;
        }

        private static string FormatTicks(long ticks, double tickMs)
        {
            return (ticks * tickMs).ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string FormatAverage(long ticks, double tickMs, double divisor)
        {
            return (ticks * tickMs / divisor).ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string MaxPhase(RuntimeCostFrame frame, double tickMs)
        {
            string name = "railEta";
            long ticks = frame.RailEta;
            SelectMax("setup", frame.Setup, ref name, ref ticks);
            SelectMax("source", frame.Source, ref name, ref ticks);
            SelectMax("stop", frame.Stop, ref name, ref ticks);
            SelectMax("bypass", frame.Bypass, ref name, ref ticks);
            SelectMax("dispatch", frame.Dispatch, ref name, ref ticks);
            SelectMax("scheduler", frame.Scheduler, ref name, ref ticks);
            SelectMax("finalize", frame.Finalize, ref name, ref ticks);
            SelectMax("maintenance", frame.Maintenance, ref name, ref ticks);
            return name + ":" + (ticks * tickMs).ToString("F2", CultureInfo.InvariantCulture);
        }

        private static void SelectMax(string candidate, long candidateTicks, ref string name, ref long ticks)
        {
            if (candidateTicks <= ticks)
                return;

            name = candidate;
            ticks = candidateTicks;
        }

        private void ClearCosts()
        {
            m_CostWindowStart = 0;
            m_CostFrames = 0;
            m_CostSlowFrames = 0;
            m_CostSevereFrames = 0;
            m_CostTotal = 0;
            m_CostRailEta = 0;
            m_CostSetup = 0;
            m_CostSource = 0;
            m_CostSourceCollect = 0;
            m_CostRegister = 0;
            m_CostSourceRoute = 0;
            m_CostStop = 0;
            m_CostBypass = 0;
            m_CostRescue = 0;
            m_CostBypassDecision = 0;
            m_CostDwellDeparture = 0;
            m_CostDispatch = 0;
            m_CostScheduler = 0;
            m_CostFinalize = 0;
            m_CostRetireSlice = 0;
            m_CostNotices = 0;
            m_CostEvents = 0;
            m_CostAnnouncements = 0;
            m_CostMaintenance = 0;
            m_CostVehicleCache = 0;
            m_CostCatalogCheck = 0;
            m_CostCatalogTick = 0;
            m_CostGc0 = 0;
            m_CostGc1 = 0;
            m_CostGc2 = 0;
            m_CostMax = 0;
            m_CostMaxFrame = default;
            m_CostMaxContext = default;
            m_CostStartFrame = 0;
            m_CostDurationFrames = 0;
            m_CostStarted = false;
            m_CostCompleted = false;
            m_CostLastSecond = 0;
            Array.Clear(m_CostSecondFrames, 0, m_CostSecondFrames.Length);
            Array.Clear(m_CostSecondCounts, 0, m_CostSecondCounts.Length);
            ResetSecondOffsets();
        }

        private void ResetSecondOffsets()
        {
            for (int i = 0; i < m_CostSecondOffsets.Length; i++)
                m_CostSecondOffsets[i] = -1;
        }

        private bool HasCounts()
        {
            return m_SourceRows > 0
                || m_FrameRows > 0
                || m_EnsureFrameRows > 0
                || m_OfficialBoardingChanged > 0
                || m_MovingChanged > 0
                || m_DeparturePendingDemand > 0
                || m_BypassWatchDemand > 0
                || m_BypassActiveDemand > 0
                || m_OriginCandidateDemand > 0
                || m_InboundWatchDemand > 0
                || m_HeavyDetailReads > 0
                || m_PathDetailReads > 0
                || m_NavigationDetailReads > 0
                || m_BusinessFacts > 0
                || m_DueDeadlines > 0
                || m_StopStagePlans > 0
                || m_BypassStagePlans > 0
                || m_DispatchStagePlans > 0
                || m_RetireStagePlans > 0
                || m_RescueStagePlans > 0
                || m_SliceStagePlans > 0
                || m_StopStageExecuted > 0
                || m_BypassStageExecuted > 0
                || m_DispatchStageExecuted > 0
                || m_RetireStageExecuted > 0
                || m_RescueStageExecuted > 0
                || m_SliceStageExecuted > 0
                || m_SchedulerExternalDirtyLines > 0
                || HasProjectionCounts();
        }

        private bool HasProjectionCounts()
        {
            return m_ProjectionCacheRequests > 0
                || m_ProjectionCalculations > 0
                || m_ProjectionPreparingLaneNotInModel > 0
                || m_ProjectionCurrentLaneReads > 0
                || m_ProjectionNavigationReads > 0
                || m_ProjectionPathReads > 0
                || m_WaypointStationConfirmed > 0
                || m_WaypointStationRejected > 0;
        }
    }
}
