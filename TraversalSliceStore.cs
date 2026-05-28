using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct TraversalSliceSamplingPlan
    {
        public readonly bool Available;
        public readonly int SegmentIndex;
        public readonly float SegmentPosition;
        public readonly uint SampleIntervalFrames;
        public readonly bool IsHighSampling;
        public readonly bool IsMediumSampling;
        public readonly bool HasUpcomingCutPoint;
        public readonly float UpcomingCutPointProgress;
        public readonly float UpcomingCutPointDistance;

        public TraversalSliceSamplingPlan(
            bool available,
            int segmentIndex,
            float segmentPosition,
            uint sampleIntervalFrames,
            bool isHighSampling,
            bool isMediumSampling,
            bool hasUpcomingCutPoint,
            float upcomingCutPointProgress,
            float upcomingCutPointDistance)
        {
            Available = available;
            SegmentIndex = segmentIndex;
            SegmentPosition = segmentPosition;
            SampleIntervalFrames = sampleIntervalFrames;
            IsHighSampling = isHighSampling;
            IsMediumSampling = isMediumSampling;
            HasUpcomingCutPoint = hasUpcomingCutPoint;
            UpcomingCutPointProgress = upcomingCutPointProgress;
            UpcomingCutPointDistance = upcomingCutPointDistance;
        }
    }

    internal readonly struct TraversalSliceObservation
    {
        public readonly float AverageFrames;
        public readonly float FastBaselineFrames;
        public readonly int SampleCount;
        public readonly uint LastObservedFrame;

        public TraversalSliceObservation(float averageFrames, float fastBaselineFrames, int sampleCount, uint lastObservedFrame)
        {
            AverageFrames = averageFrames;
            FastBaselineFrames = fastBaselineFrames;
            SampleCount = sampleCount;
            LastObservedFrame = lastObservedFrame;
        }
    }

    internal readonly struct TraversalSliceSamplingPlanCache
    {
        public readonly Entity Line;
        public readonly ulong ChainSignature;
        public readonly int SliceIndex;
        public readonly uint NextRefreshFrame;
        public readonly TraversalSliceSamplingPlan Plan;

        public TraversalSliceSamplingPlanCache(Entity line, ulong chainSignature, int sliceIndex, uint nextRefreshFrame, TraversalSliceSamplingPlan plan)
        {
            Line = line;
            ChainSignature = chainSignature;
            SliceIndex = sliceIndex;
            NextRefreshFrame = nextRefreshFrame;
            Plan = plan;
        }
    }

    internal readonly struct VehicleTraversalSliceSession
    {
        public readonly Entity Line;
        public readonly int SliceIndex;
        public readonly uint EnterFrame;
        public readonly int EnterAtomIndex;
        public readonly float EnterAtomPosition01;

        public VehicleTraversalSliceSession(Entity line, int sliceIndex, uint enterFrame, int enterAtomIndex, float enterAtomPosition01)
        {
            Line = line;
            SliceIndex = sliceIndex;
            EnterFrame = enterFrame;
            EnterAtomIndex = enterAtomIndex;
            EnterAtomPosition01 = enterAtomPosition01;
        }
    }

    internal readonly struct TraversalSliceActualSample
    {
        public readonly Entity Line;
        public readonly Entity Vehicle;
        public readonly int SliceIndex;
        public readonly uint EnterFrame;
        public readonly uint ExitFrame;
        public readonly int EnterAtomIndex;
        public readonly float EnterAtomPosition01;
        public readonly int ExitAtomIndex;
        public readonly float ExitAtomPosition01;

        public TraversalSliceActualSample(
            Entity line,
            Entity vehicle,
            int sliceIndex,
            uint enterFrame,
            uint exitFrame,
            int enterAtomIndex,
            float enterAtomPosition01,
            int exitAtomIndex,
            float exitAtomPosition01)
        {
            Line = line;
            Vehicle = vehicle;
            SliceIndex = sliceIndex;
            EnterFrame = enterFrame;
            ExitFrame = exitFrame;
            EnterAtomIndex = enterAtomIndex;
            EnterAtomPosition01 = enterAtomPosition01;
            ExitAtomIndex = exitAtomIndex;
            ExitAtomPosition01 = exitAtomPosition01;
        }
    }

    internal readonly struct TraversalPositionSample
    {
        public readonly Entity Line;
        public readonly Entity Vehicle;
        public readonly uint Frame;
        public readonly int SliceIndex;
        public readonly int SegmentIndex;
        public readonly float SegmentPosition;
        public readonly int AtomIndex;
        public readonly float AtomPosition01;
        public readonly Entity PhysicalLane;
        public readonly float SpeedMetersPerSecond;
        public readonly float OdometerMeters;

        public TraversalPositionSample(
            Entity line,
            Entity vehicle,
            uint frame,
            int sliceIndex,
            int segmentIndex,
            float segmentPosition,
            int atomIndex,
            float atomPosition01,
            Entity physicalLane,
            float speedMetersPerSecond,
            float odometerMeters)
        {
            Line = line;
            Vehicle = vehicle;
            Frame = frame;
            SliceIndex = sliceIndex;
            SegmentIndex = segmentIndex;
            SegmentPosition = segmentPosition;
            AtomIndex = atomIndex;
            AtomPosition01 = atomPosition01;
            PhysicalLane = physicalLane;
            SpeedMetersPerSecond = speedMetersPerSecond;
            OdometerMeters = odometerMeters;
        }
    }

    internal struct TraversalSliceLapDebugAggregate
    {
        public int StartCount;
        public int FinalizeCount;
        public int MidSliceStartCount;
        public int DroppedWithoutFinalizeCount;
        public float EnterOffsetSumAtoms;
        public float MaxEnterOffsetAtoms;
        public float ObservedFramesSum;
        public float MinObservedFrames;
        public float MaxObservedFrames;

        public void RecordStart(float enterOffsetAtoms, bool midSliceStart)
        {
            StartCount++;
            if (midSliceStart) MidSliceStartCount++;
            EnterOffsetSumAtoms += enterOffsetAtoms;
            if (enterOffsetAtoms > MaxEnterOffsetAtoms) MaxEnterOffsetAtoms = enterOffsetAtoms;
        }

        public void RecordFinalize(float observedFrames)
        {
            FinalizeCount++;
            ObservedFramesSum += observedFrames;
            if (FinalizeCount == 1 || observedFrames < MinObservedFrames) MinObservedFrames = observedFrames;
            if (observedFrames > MaxObservedFrames) MaxObservedFrames = observedFrames;
        }

        public void RecordDropped() => DroppedWithoutFinalizeCount++;
    }

    internal sealed class TraversalSliceStore
    {
        private const int MaxRecentActualSamples = 4096;
        private const int MaxRecentPositionSamples = 4096;

        private readonly Dictionary<ulong, TraversalSliceObservation> m_TraversalRunSliceObservations =
            new Dictionary<ulong, TraversalSliceObservation>();
        private readonly Dictionary<Entity, VehicleTraversalSliceSession> m_VehicleTraversalSliceSessions =
            new Dictionary<Entity, VehicleTraversalSliceSession>();
        private readonly Dictionary<Entity, uint> m_VehicleTraversalSliceLastSampleFrame =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_VehicleTraversalPositionLastSampleFrame =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, TraversalSliceSamplingPlanCache> m_VehicleTraversalSliceSamplingPlans =
            new Dictionary<Entity, TraversalSliceSamplingPlanCache>();
        private readonly Dictionary<ulong, TraversalSliceLapDebugAggregate> m_VehicleTraversalSliceLapDebug =
            new Dictionary<ulong, TraversalSliceLapDebugAggregate>();
        private readonly List<TraversalSliceActualSample> m_RecentActualSamples =
            new List<TraversalSliceActualSample>();
        private readonly List<TraversalPositionSample> m_RecentPositionSamples =
            new List<TraversalPositionSample>();

        internal Dictionary<ulong, TraversalSliceObservation> Observations => m_TraversalRunSliceObservations;
        internal Dictionary<Entity, VehicleTraversalSliceSession> Sessions => m_VehicleTraversalSliceSessions;
        internal Dictionary<Entity, uint> LastSampleFrames => m_VehicleTraversalSliceLastSampleFrame;
        internal Dictionary<Entity, uint> LastPositionSampleFrames => m_VehicleTraversalPositionLastSampleFrame;
        internal Dictionary<Entity, TraversalSliceSamplingPlanCache> Plans => m_VehicleTraversalSliceSamplingPlans;
        internal Dictionary<ulong, TraversalSliceLapDebugAggregate> LapDebug => m_VehicleTraversalSliceLapDebug;
        internal List<TraversalSliceActualSample> RecentActualSamples => m_RecentActualSamples;
        internal List<TraversalPositionSample> RecentPositionSamples => m_RecentPositionSamples;

        internal void Clear()
        {
            m_TraversalRunSliceObservations.Clear();
            m_VehicleTraversalSliceSessions.Clear();
            m_VehicleTraversalSliceLastSampleFrame.Clear();
            m_VehicleTraversalPositionLastSampleFrame.Clear();
            m_VehicleTraversalSliceSamplingPlans.Clear();
            m_VehicleTraversalSliceLapDebug.Clear();
            m_RecentActualSamples.Clear();
            m_RecentPositionSamples.Clear();
        }

        internal void Remove(Entity vehicle)
        {
            m_VehicleTraversalSliceSessions.Remove(vehicle);
            m_VehicleTraversalSliceLastSampleFrame.Remove(vehicle);
            m_VehicleTraversalPositionLastSampleFrame.Remove(vehicle);
            m_VehicleTraversalSliceSamplingPlans.Remove(vehicle);
        }

        internal bool TryObservation(ulong key, out TraversalSliceObservation observation) =>
            m_TraversalRunSliceObservations.TryGetValue(key, out observation);

        internal void Record(ulong key, TraversalSliceObservation observation) =>
            m_TraversalRunSliceObservations[key] = observation;

        internal void RecordActualSample(TraversalSliceActualSample sample)
        {
            m_RecentActualSamples.Add(sample);
            TrimRecentSamples(m_RecentActualSamples, MaxRecentActualSamples);
        }

        internal void RecordPositionSample(TraversalPositionSample sample)
        {
            m_RecentPositionSamples.Add(sample);
            TrimRecentSamples(m_RecentPositionSamples, MaxRecentPositionSamples);
        }

        private static void TrimRecentSamples<T>(List<T> samples, int maxCount)
        {
            if (samples.Count <= maxCount)
                return;

            int removeCount = samples.Count - maxCount;
            samples.RemoveRange(0, removeCount);
        }
    }
}
