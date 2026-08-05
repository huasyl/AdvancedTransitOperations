using RapidTransitMod.Runtime;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal sealed class RuntimeHotPathProbe
    {
        private const uint FlushIntervalFrames = 3600;
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

        internal RuntimeHotPathProbe(TimedLogger log)
        {
            m_Log = log;
        }

        internal static bool Enabled() => RtLog.VerboseEnabled;

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

            Clear();
            m_LastFlushFrame = nowFrame;
        }

        internal void Clear()
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
