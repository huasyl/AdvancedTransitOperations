using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Runtime;
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
        private readonly Func<Entity, bool> m_IsLinePending;

        internal MonitorAverageStore(Func<Entity, bool> isLinePending = null)
        {
            m_IsLinePending = isLinePending;
        }

        internal MonitorChange Add(MonitorIntervalSample sample)
        {
            if (!ValidSample(sample)
                || (m_IsLinePending != null && m_IsLinePending(sample.Line)))
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

        internal int RemoveLine(Entity line)
        {
            if (line == Entity.Null || !m_Lines.TryGetValue(line, out MonitorAverageLine value))
                return 0;

            m_Lines.Remove(line);
            return value.Segments?.Length ?? 0;
        }

        internal MonitorAverageRemapResult RemapLine(
            Entity line,
            LineStopLayout newLayout,
            LineIntervalImpact impact,
            string oldStopSig)
        {
            if (line == Entity.Null)
                return default;
            if (newLayout != null
                && !string.Equals(oldStopSig, newLayout.StopSig, StringComparison.Ordinal)
                && m_Lines.TryGetValue(line, out MonitorAverageLine currentLine)
                && string.Equals(currentLine.StopSig, newLayout.StopSig, StringComparison.Ordinal))
                return default;
            if (newLayout == null || newLayout.StopCount < 2 || impact == null || !impact.IsValid)
            {
                int removed = RemoveLine(line);
                return new MonitorAverageRemapResult(removed, 0, removed);
            }
            if (!m_Lines.TryGetValue(line, out MonitorAverageLine oldLine)
                || oldLine.Segments == null
                || oldLine.Segments.Length == 0
                || !string.Equals(oldLine.StopSig, oldStopSig, StringComparison.Ordinal))
            {
                int removed = RemoveLine(line);
                return new MonitorAverageRemapResult(removed, 0, removed);
            }

            int count = newLayout.StopCount;
            if (count > MaxSegmentsPerLine)
            {
                int removed = RemoveLine(line);
                return new MonitorAverageRemapResult(removed, 0, removed);
            }

            MonitorAverageSegment[] segments = new MonitorAverageSegment[count];
            int zeroed = 0;
            int retained = 0;
            for (int newIndex = 0; newIndex < count; newIndex++)
            {
                int oldIndex = impact.NewToOld(newIndex);
                if (oldIndex >= 0
                    && oldIndex < oldLine.Segments.Length
                    && !impact.IsNewAffected(newIndex))
                {
                    segments[newIndex] = oldLine.Segments[oldIndex];
                    retained++;
                }
                else
                {
                    zeroed++;
                }
            }

            ulong revision = oldLine.Revision == ulong.MaxValue ? 1UL : oldLine.Revision + 1UL;
            m_Lines[line] = new MonitorAverageLine(
                line,
                newLayout.StopSig,
                revision,
                segments,
                HasCompleteCoverage(segments));
            return new MonitorAverageRemapResult(zeroed, retained, 0);
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

    internal readonly struct MonitorAverageRemapResult
    {
        internal readonly int ZeroedIntervals;
        internal readonly int RetainedIntervals;
        internal readonly int RemovedIntervals;

        internal MonitorAverageRemapResult(int zeroedIntervals, int retainedIntervals, int removedIntervals)
        {
            ZeroedIntervals = zeroedIntervals;
            RetainedIntervals = retainedIntervals;
            RemovedIntervals = removedIntervals;
        }
    }
}
