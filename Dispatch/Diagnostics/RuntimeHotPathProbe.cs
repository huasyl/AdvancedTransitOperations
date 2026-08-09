using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RapidTransitMod.Runtime;

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
                || m_SchedulerExternalDirtyLines > 0;
        }
    }
}
