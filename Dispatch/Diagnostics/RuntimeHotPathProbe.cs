using Unity.Entities;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal sealed class RuntimeHotPathProbe
    {
        private const uint FlushIntervalFrames = 3600;
        private readonly TimedLogger m_Log;
        private uint m_LastFlushFrame;

        internal ulong RunningTotal;
        internal ulong RunningBoardingChanged;
        internal ulong RunningBoarding;
        internal ulong RunningMidStopBoarding;
        internal ulong RunningWaypointNegative;
        internal ulong RunningWaypointOrigin;
        internal ulong RunningWaypointPositive;
        internal ulong BypassSkipped;
        internal ulong BypassLatched;
        internal ulong BypassEarlyReturn;
        internal ulong BypassEvaluated;
        internal ulong BypassEvaluateDepartureGateCalls;
        internal ulong BypassScopeMisses;
        internal ulong BypassTryGetLocalSceneMisses;
        internal ulong BypassFastSkipNonBoarding;
        internal ulong BypassPreparedLatched;
        internal ulong BypassPreparedSkipped;
        internal ulong BypassScenePrecheckCalls;
        internal ulong BypassScenePrecheckEligible;
        internal ulong BypassScenePrecheckSkipped;
        internal ulong BypassScenePrecheckUnknown;
        internal ulong DwellDeadlineCacheHit;
        internal ulong DwellDeadlineCacheMiss;
        internal ulong SliceEntryProbeSkip;
        internal ulong SliceSampleDue;
        internal ulong SliceSampleNonDue;
        internal ulong OriginSettleCalls;
        internal ulong OriginSettleFastPathHits;
        internal ulong OriginSettleSlowPathEntered;
        internal ulong OriginSettleCandidateHits;
        internal ulong OriginSettleWindowHits;

        internal RuntimeHotPathProbe(TimedLogger log)
        {
            m_Log = log;
        }

        internal static bool Enabled() => RtLog.VerboseEnabled;

        internal void CountRunning(bool boardingChanged, bool boarding, bool midStopBoarding, int waypointIndex)
        {
            if (!Enabled())
                return;

            RunningTotal++;
            if (boardingChanged) RunningBoardingChanged++;
            if (boarding) RunningBoarding++;
            if (midStopBoarding) RunningMidStopBoarding++;
            if (waypointIndex < 0) RunningWaypointNegative++;
            else if (waypointIndex == 0) RunningWaypointOrigin++;
            else RunningWaypointPositive++;
        }

        internal void CountBypassSkipped()
        {
            if (Enabled()) BypassSkipped++;
        }

        internal void CountBypassLatched()
        {
            if (Enabled()) BypassLatched++;
        }

        internal void CountBypassEarlyReturn()
        {
            if (Enabled()) BypassEarlyReturn++;
        }

        internal void CountBypassEvaluated()
        {
            if (Enabled()) BypassEvaluated++;
        }

        internal void CountEvaluateDepartureGate()
        {
            if (Enabled()) BypassEvaluateDepartureGateCalls++;
        }

        internal void CountScopeMiss()
        {
            if (Enabled()) BypassScopeMisses++;
        }

        internal void CountTryGetLocalSceneMiss()
        {
            if (Enabled()) BypassTryGetLocalSceneMisses++;
        }

        internal void CountBypassFastSkipNonBoarding()
        {
            if (Enabled()) BypassFastSkipNonBoarding++;
        }

        internal void CountBypassPreparedLatched()
        {
            if (Enabled()) BypassPreparedLatched++;
        }

        internal void CountBypassPreparedSkipped()
        {
            if (Enabled()) BypassPreparedSkipped++;
        }

        internal void CountBypassScenePrecheckCall()
        {
            if (Enabled()) BypassScenePrecheckCalls++;
        }

        internal void CountBypassScenePrecheckEligible()
        {
            if (Enabled()) BypassScenePrecheckEligible++;
        }

        internal void CountBypassScenePrecheckSkipped()
        {
            if (Enabled()) BypassScenePrecheckSkipped++;
        }

        internal void CountBypassScenePrecheckUnknown()
        {
            if (Enabled()) BypassScenePrecheckUnknown++;
        }

        internal void CountDwellDeadlineCacheHit()
        {
            if (Enabled()) DwellDeadlineCacheHit++;
        }

        internal void CountDwellDeadlineCacheMiss()
        {
            if (Enabled()) DwellDeadlineCacheMiss++;
        }

        internal void CountSliceEntryProbeSkip()
        {
            if (Enabled()) SliceEntryProbeSkip++;
        }

        internal void CountSliceSampleDue()
        {
            if (Enabled()) SliceSampleDue++;
        }

        internal void CountSliceSampleNonDue()
        {
            if (Enabled()) SliceSampleNonDue++;
        }

        internal void CountOriginSettleCall()
        {
            if (Enabled()) OriginSettleCalls++;
        }

        internal void CountOriginSettleFastPath(bool candidate)
        {
            if (!Enabled())
                return;

            OriginSettleFastPathHits++;
            if (candidate) OriginSettleCandidateHits++;
        }

        internal void CountOriginSettleSlowPath()
        {
            if (Enabled()) OriginSettleSlowPathEntered++;
        }

        internal void CountOriginSettleWindowHit()
        {
            if (Enabled()) OriginSettleWindowHits++;
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
                    + " running=" + RunningTotal
                    + " boardingChanged=" + RunningBoardingChanged
                    + " boarding=" + RunningBoarding
                    + " midStopBoarding=" + RunningMidStopBoarding
                    + " wpNeg=" + RunningWaypointNegative
                    + " wp0=" + RunningWaypointOrigin
                    + " wpPos=" + RunningWaypointPositive
                    + " bypassSkipped=" + BypassSkipped
                    + " bypassLatched=" + BypassLatched
                    + " bypassEarlyReturn=" + BypassEarlyReturn
                    + " bypassEvaluated=" + BypassEvaluated
                    + " evalGate=" + BypassEvaluateDepartureGateCalls
                    + " scopeMiss=" + BypassScopeMisses
                    + " localSceneMiss=" + BypassTryGetLocalSceneMisses
                    + " bypassFastSkipNonBoarding=" + BypassFastSkipNonBoarding
                    + " bypassPreparedLatched=" + BypassPreparedLatched
                    + " bypassPreparedSkipped=" + BypassPreparedSkipped
                    + " bypassScenePrecheckCalls=" + BypassScenePrecheckCalls
                    + " bypassScenePrecheckEligible=" + BypassScenePrecheckEligible
                    + " bypassScenePrecheckSkipped=" + BypassScenePrecheckSkipped
                    + " bypassScenePrecheckUnknown=" + BypassScenePrecheckUnknown
                    + " dwellDeadlineCacheHit=" + DwellDeadlineCacheHit
                    + " dwellDeadlineCacheMiss=" + DwellDeadlineCacheMiss
                    + " sliceEntryProbeSkip=" + SliceEntryProbeSkip
                    + " sliceDue=" + SliceSampleDue
                    + " sliceNonDue=" + SliceSampleNonDue
                    + " originCalls=" + OriginSettleCalls
                    + " originFast=" + OriginSettleFastPathHits
                    + " originSlow=" + OriginSettleSlowPathEntered
                    + " originCandidate=" + OriginSettleCandidateHits
                    + " originWindow=" + OriginSettleWindowHits);
            }

            Clear();
            m_LastFlushFrame = nowFrame;
        }

        internal void Clear()
        {
            m_LastFlushFrame = 0;
            RunningTotal = 0;
            RunningBoardingChanged = 0;
            RunningBoarding = 0;
            RunningMidStopBoarding = 0;
            RunningWaypointNegative = 0;
            RunningWaypointOrigin = 0;
            RunningWaypointPositive = 0;
            BypassSkipped = 0;
            BypassLatched = 0;
            BypassEarlyReturn = 0;
            BypassEvaluated = 0;
            BypassEvaluateDepartureGateCalls = 0;
            BypassScopeMisses = 0;
            BypassTryGetLocalSceneMisses = 0;
            BypassFastSkipNonBoarding = 0;
            BypassPreparedLatched = 0;
            BypassPreparedSkipped = 0;
            BypassScenePrecheckCalls = 0;
            BypassScenePrecheckEligible = 0;
            BypassScenePrecheckSkipped = 0;
            BypassScenePrecheckUnknown = 0;
            DwellDeadlineCacheHit = 0;
            DwellDeadlineCacheMiss = 0;
            SliceEntryProbeSkip = 0;
            SliceSampleDue = 0;
            SliceSampleNonDue = 0;
            OriginSettleCalls = 0;
            OriginSettleFastPathHits = 0;
            OriginSettleSlowPathEntered = 0;
            OriginSettleCandidateHits = 0;
            OriginSettleWindowHits = 0;
        }

        private bool HasCounts()
        {
            return RunningTotal > 0
                || BypassSkipped > 0
                || BypassLatched > 0
                || BypassEarlyReturn > 0
                || BypassEvaluated > 0
                || BypassEvaluateDepartureGateCalls > 0
                || BypassScopeMisses > 0
                || BypassTryGetLocalSceneMisses > 0
                || BypassFastSkipNonBoarding > 0
                || BypassPreparedLatched > 0
                || BypassPreparedSkipped > 0
                || BypassScenePrecheckCalls > 0
                || BypassScenePrecheckEligible > 0
                || BypassScenePrecheckSkipped > 0
                || BypassScenePrecheckUnknown > 0
                || DwellDeadlineCacheHit > 0
                || DwellDeadlineCacheMiss > 0
                || SliceEntryProbeSkip > 0
                || SliceSampleDue > 0
                || SliceSampleNonDue > 0
                || OriginSettleCalls > 0;
        }
    }
}
