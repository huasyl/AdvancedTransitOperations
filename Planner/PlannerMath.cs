using System;
using System.Collections.Generic;
using System.Globalization;

namespace RapidTransitMod.Planner
{
    internal static class PlannerMath
    {
        public static int? TimeToMinutes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            string[] parts = value.Split(':');
            if (parts.Length != 2)
            {
                return null;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute))
            {
                return null;
            }

            if (hour < 0 || hour > 47 || minute < 0 || minute > 59)
            {
                return null;
            }

            return (hour * 60) + minute;
        }

        public static string MinutesToTime(int minute)
        {
            int normalized = minute % 1440;
            if (normalized < 0)
            {
                normalized += 1440;
            }

            int hour = normalized / 60;
            int minutePart = normalized % 60;
            return hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minutePart.ToString("00", CultureInfo.InvariantCulture);
        }

        public static float ComputeForwardMinuteDelta(string startTime, string endTime)
        {
            int? startMinute = TimeToMinutes(startTime);
            int? endMinute = TimeToMinutes(endTime);
            if (!startMinute.HasValue || !endMinute.HasValue)
            {
                return -1f;
            }

            int delta = endMinute.Value - startMinute.Value;
            if (delta < -720)
            {
                delta += 1440;
            }

            return delta >= 0 ? delta : -1f;
        }

        public static float EstimateVariabilityMinutes(float baseMinutes, float confidence, int sampleCount, float fastMinutes)
        {
            float safeBaseMinutes = Math.Max(0f, baseMinutes);
            if (!(safeBaseMinutes > 0f))
            {
                return 0f;
            }

            float variabilityMinutes = 0f;
            if (fastMinutes > 0f)
            {
                variabilityMinutes = Math.Max(variabilityMinutes, Math.Abs(safeBaseMinutes - fastMinutes));
            }

            float confidenceGap = Math.Max(0f, 1f - Math.Max(0.2f, confidence));
            float samplePenalty = sampleCount > 0
                ? Math.Min(1f, 3f / Math.Max(1, sampleCount))
                : 1f;
            variabilityMinutes = Math.Max(
                variabilityMinutes,
                safeBaseMinutes * (0.12f + (confidenceGap * 0.28f * samplePenalty)));
            return Round4(variabilityMinutes);
        }

        public static PlannerObservedRuntimeSummary SummarizeRuntimeSamples(List<float> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return null;
            }

            List<float> ordered = new List<float>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] >= 0f && !float.IsNaN(samples[i]) && !float.IsInfinity(samples[i]))
                {
                    ordered.Add(samples[i]);
                }
            }

            if (ordered.Count == 0)
            {
                return null;
            }

            ordered.Sort();
            int lowerIndex = (int)Math.Floor((ordered.Count - 1) * 0.25d);
            int medianIndex = ordered.Count / 2;
            int upperIndex = (int)Math.Floor((ordered.Count - 1) * 0.75d);
            float fastMinutes = ordered[lowerIndex];
            float medianMinutes = ordered[medianIndex];
            float upperMinutes = ordered[upperIndex];
            float total = 0f;
            for (int i = 0; i < ordered.Count; i++)
            {
                total += ordered[i];
            }

            float averageMinutes = total / ordered.Count;
            float confidence = Math.Min(0.9f, 0.48f + (ordered.Count * 0.06f));
            float coreSpreadMinutes = Math.Max(0f, upperMinutes - fastMinutes);
            float medianGapMinutes = Math.Max(0f, medianMinutes - fastMinutes);
            float variabilityMinutes = Math.Max(
                1f,
                Math.Min(fastMinutes * 0.3f, (coreSpreadMinutes * 0.5f) + (medianGapMinutes * 0.25f)));

            return new PlannerObservedRuntimeSummary
            {
                Minutes = Round2(fastMinutes),
                MedianMinutes = Round2(medianMinutes),
                AverageMinutes = Round2(averageMinutes),
                MinMinutes = Round2(ordered[0]),
                MaxMinutes = Round2(ordered[ordered.Count - 1]),
                Confidence = Round2(confidence),
                VariabilityMinutes = Round2(variabilityMinutes),
                SampleCount = ordered.Count,
                BaselinePolicy = "fastObservedQuartile",
                Source = "tripObserved"
            };
        }

        public static bool IsMinuteInsideWindow(int minute, int? windowStartMinute, int? windowEndMinute)
        {
            if (!windowStartMinute.HasValue || !windowEndMinute.HasValue)
            {
                return true;
            }

            return minute >= windowStartMinute.Value && minute < windowEndMinute.Value;
        }

        public static List<int> GeneratePeriodicMinutes(int windowStartMinute, int windowEndMinute, float tripsPerHour, int? anchorMinute)
        {
            List<int> result = new List<int>();
            if (windowEndMinute <= windowStartMinute || !(tripsPerHour > 0f))
            {
                return result;
            }

            float intervalMinutes = 60f / tripsPerHour;
            float firstMinute = windowStartMinute;
            if (anchorMinute.HasValue)
            {
                int intervalsFromAnchor = (int)Math.Ceiling((windowStartMinute - anchorMinute.Value) / intervalMinutes);
                firstMinute = anchorMinute.Value + (Math.Max(0, intervalsFromAnchor) * intervalMinutes);
                while (firstMinute - intervalMinutes >= windowStartMinute)
                {
                    firstMinute -= intervalMinutes;
                }
            }

            for (float minute = firstMinute; minute < windowEndMinute; minute += intervalMinutes)
            {
                if (minute < windowStartMinute)
                {
                    continue;
                }

                result.Add((int)Math.Round(minute));
            }

            return result;
        }

        public static float Round2(float value)
        {
            return (float)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        public static float Round4(float value)
        {
            return (float)Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }
    }
}
