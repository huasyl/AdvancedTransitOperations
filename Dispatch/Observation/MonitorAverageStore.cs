using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct MonitorIntervalSample
    {
        internal readonly Entity Line;
        internal readonly string StopSig;
        internal readonly int StopCount;
        internal readonly int FromOrder;
        internal readonly int ToOrder;
        internal readonly uint Frames;
        internal readonly bool Closing;

        internal MonitorIntervalSample(
            Entity line,
            string stopSig,
            int stopCount,
            int fromOrder,
            int toOrder,
            uint frames,
            bool closing)
        {
            Line = line;
            StopSig = stopSig ?? string.Empty;
            StopCount = stopCount;
            FromOrder = fromOrder;
            ToOrder = toOrder;
            Frames = frames;
            Closing = closing;
        }
    }

    internal readonly struct MonitorStopResult
    {
        internal readonly bool Accepted;
        internal readonly Entity Line;
        internal readonly int ServiceDateKey;
        internal readonly string TripKey;
        internal readonly MonitorIntervalSample Sample;

        internal MonitorStopResult(
            bool accepted,
            Entity line,
            int serviceDateKey,
            string tripKey,
            MonitorIntervalSample sample)
        {
            Accepted = accepted;
            Line = line;
            ServiceDateKey = serviceDateKey;
            TripKey = tripKey ?? string.Empty;
            Sample = sample;
        }
    }

    internal readonly struct MonitorChange
    {
        internal readonly bool Changed;
        internal readonly Entity Line;
        internal readonly int ServiceDateKey;
        internal readonly string TripKey;
        internal readonly ulong MonitorRevision;
        internal readonly bool MonitorAverageBecameReady;

        internal MonitorChange(
            bool changed,
            Entity line,
            int serviceDateKey,
            string tripKey,
            ulong monitorRevision,
            bool monitorAverageBecameReady)
        {
            Changed = changed;
            Line = line;
            ServiceDateKey = serviceDateKey;
            TripKey = tripKey ?? string.Empty;
            MonitorRevision = monitorRevision;
            MonitorAverageBecameReady = monitorAverageBecameReady;
        }
    }

    internal sealed class MonitorAverageStore
    {
        internal const int MaxLines = 4096;
        internal const int MaxSegmentsPerLine = 256;
        private const int MaxSamplesPerSegment = 65536;
        private readonly Dictionary<Entity, MonitorAverageLine> m_Lines =
            new Dictionary<Entity, MonitorAverageLine>();

        internal MonitorChange Add(MonitorIntervalSample sample)
        {
            if (!ValidSample(sample))
                return default;

            if (!m_Lines.TryGetValue(sample.Line, out MonitorAverageLine line))
            {
                if (m_Lines.Count >= MaxLines)
                    return default;
                line = new MonitorAverageLine(sample.Line, sample.StopSig, sample.StopCount);
                m_Lines[sample.Line] = line;
            }
            else if (!string.Equals(line.StopSig, sample.StopSig, StringComparison.Ordinal)
                || line.Segments.Length != sample.StopCount)
            {
                return default;
            }

            int index = sample.FromOrder;
            MonitorAverageSegment segment = line.Segments[index];
            if (segment.SampleCount >= MaxSamplesPerSegment
                || ulong.MaxValue - segment.TotalFrames < sample.Frames)
            {
                return default;
            }

            bool hadCoverage = segment.SampleCount > 0;
            segment.TotalFrames += sample.Frames;
            segment.SampleCount++;
            line.Segments[index] = segment;
            line.Revision++;
            bool becameReady = false;
            if (!line.Ready && !hadCoverage && HasCompleteCoverage(line.Segments))
            {
                line.Ready = true;
                becameReady = true;
            }
            return new MonitorChange(true, line.Line, 0, string.Empty, line.Revision, becameReady);
        }

        internal bool TryState(Entity line, string expectedStopSig, out MonitorAverageState state)
        {
            state = default;
            if (line == Entity.Null
                || !m_Lines.TryGetValue(line, out MonitorAverageLine value)
                || (!string.IsNullOrEmpty(expectedStopSig)
                    && !string.Equals(value.StopSig, expectedStopSig, StringComparison.Ordinal)))
            {
                return false;
            }
            state = new MonitorAverageState(value.StopSig, value.Ready, value.Revision);
            return true;
        }

        internal bool TrySnapshot(Entity line, string expectedStopSig, out MonitorAverageSnapshot snapshot)
        {
            snapshot = default;
            if (!TryState(line, expectedStopSig, out MonitorAverageState state)
                || !state.Ready
                || !m_Lines.TryGetValue(line, out MonitorAverageLine value))
            {
                return false;
            }

            double[] averageFrames = new double[value.Segments.Length];
            for (int i = 0; i < value.Segments.Length; i++)
            {
                MonitorAverageSegment segment = value.Segments[i];
                if (segment.SampleCount <= 0 || segment.TotalFrames == 0)
                    return false;
                averageFrames[i] = (double)segment.TotalFrames / segment.SampleCount;
                if (!(averageFrames[i] > 0d)
                    || double.IsNaN(averageFrames[i])
                    || double.IsInfinity(averageFrames[i]))
                    return false;
            }
            snapshot = new MonitorAverageSnapshot(value.StopSig, value.Revision, averageFrames);
            return true;
        }

        internal IEnumerable<MonitorAverageLine> Lines => m_Lines.Values;

        internal bool Restore(MonitorAverageLine value)
        {
            if (value == null
                || value.Line == Entity.Null
                || string.IsNullOrEmpty(value.StopSig)
                || value.Segments == null
                || value.Segments.Length == 0
                || value.Segments.Length > MaxSegmentsPerLine
                || m_Lines.ContainsKey(value.Line))
            {
                return false;
            }

            MonitorAverageSegment[] copy = new MonitorAverageSegment[value.Segments.Length];
            for (int i = 0; i < value.Segments.Length; i++)
            {
                MonitorAverageSegment segment = value.Segments[i];
                if ((segment.SampleCount == 0 && segment.TotalFrames != 0)
                    || (segment.SampleCount > 0 && segment.TotalFrames == 0)
                    || segment.SampleCount < 0
                    || segment.SampleCount > MaxSamplesPerSegment)
                {
                    return false;
                }
                copy[i] = segment;
            }

            m_Lines[value.Line] = new MonitorAverageLine(
                value.Line,
                value.StopSig,
                value.Revision,
                copy,
                HasCompleteCoverage(copy));
            return true;
        }

        internal void RemoveLine(Entity line)
        {
            if (line != Entity.Null)
                m_Lines.Remove(line);
        }

        internal void Clear()
        {
            m_Lines.Clear();
        }

        private static bool ValidSample(MonitorIntervalSample sample)
        {
            if (sample.Line == Entity.Null
                || string.IsNullOrEmpty(sample.StopSig)
                || sample.StopCount < 2
                || sample.StopCount > MaxSegmentsPerLine
                || sample.FromOrder < 0
                || sample.FromOrder >= sample.StopCount
                || sample.ToOrder < 0
                || sample.ToOrder >= sample.StopCount
                || sample.Frames == 0u
                || sample.Frames >= 0x80000000u)
            {
                return false;
            }
            return sample.Closing
                ? sample.FromOrder == sample.StopCount - 1 && sample.ToOrder == 0
                : sample.ToOrder == sample.FromOrder + 1;
        }

        private static bool HasCompleteCoverage(MonitorAverageSegment[] segments)
        {
            for (int i = 0; i < segments.Length; i++)
                if (segments[i].SampleCount <= 0)
                    return false;
            return true;
        }
    }

    internal sealed class MonitorAverageLine
    {
        internal readonly Entity Line;
        internal readonly string StopSig;
        internal ulong Revision;
        internal readonly MonitorAverageSegment[] Segments;
        internal bool Ready;

        internal MonitorAverageLine(Entity line, string stopSig, int segmentCount)
        {
            Line = line;
            StopSig = stopSig ?? string.Empty;
            Segments = new MonitorAverageSegment[segmentCount];
        }

        internal MonitorAverageLine(
            Entity line,
            string stopSig,
            ulong revision,
            MonitorAverageSegment[] segments,
            bool ready)
        {
            Line = line;
            StopSig = stopSig ?? string.Empty;
            Revision = revision;
            Segments = segments ?? Array.Empty<MonitorAverageSegment>();
            Ready = ready;
        }
    }

    internal struct MonitorAverageSegment
    {
        internal ulong TotalFrames;
        internal int SampleCount;
    }

    internal readonly struct MonitorAverageState
    {
        internal readonly string StopSig;
        internal readonly bool Ready;
        internal readonly ulong Revision;

        internal MonitorAverageState(string stopSig, bool ready, ulong revision)
        {
            StopSig = stopSig ?? string.Empty;
            Ready = ready;
            Revision = revision;
        }
    }

    internal readonly struct MonitorAverageSnapshot
    {
        internal readonly string StopSig;
        internal readonly ulong Revision;
        internal readonly double[] AverageFrames;

        internal MonitorAverageSnapshot(string stopSig, ulong revision, double[] averageFrames)
        {
            StopSig = stopSig ?? string.Empty;
            Revision = revision;
            AverageFrames = averageFrames ?? Array.Empty<double>();
        }
    }
}
