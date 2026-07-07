using System;
using Game.Prefabs;

namespace RapidTransitMod.RailTravel
{
    internal sealed class Request
    {
        public Request()
        {
            CoupledUnits = Array.Empty<TrainData>();
            MaxTicks = 200000;
            StopAtEnd = true;
        }

        public string RequestId { get; set; } = string.Empty;
        public Path Path { get; set; }
        public TrainData LeadUnit { get; set; }
        public TrainData[] CoupledUnits { get; set; }
        public float InitialSpeed { get; set; }
        public bool StopAtEnd { get; set; }
        public int MaxTicks { get; set; }
    }

    internal sealed class Diagnostics
    {
        public float PathLength { get; set; }
        public int SegmentCount { get; set; }
        public int ConnectionSegmentCount { get; set; }
        public int SourceElementCount { get; set; }
        public int SkippedElementCount { get; set; }
        public int ConnectionTicks { get; set; }
        public int DriveLimitedTicks { get; set; }
        public int BrakingLimitedTicks { get; set; }
        public float PeakSpeed { get; set; }
        public float TrainLength { get; set; }
        public float FinalRemainingDistance { get; set; }
        public bool HitTickLimit { get; set; }
        public TrainData EffectiveTrain { get; set; }
    }

    internal sealed class Result
    {
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;
        public float Distance { get; set; }
        public float Duration { get; set; }
        public int TickCount { get; set; }
        public float ExitSpeed { get; set; }
        public Diagnostics Diagnostics { get; set; } = new Diagnostics();
    }
}
