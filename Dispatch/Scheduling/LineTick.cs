using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal readonly struct LineTick
    {
        public readonly Entity Line;
        public readonly DynamicBuffer<RouteWaypoint> Ways;
        public readonly IReadOnlyList<Entity> Vehicles;
        public readonly int NowMinute;
        public readonly uint NowFrame;
        public readonly IReadOnlyList<int> Targets;
        public readonly int HoldMinutes;
        public readonly float LapFrames;
        public readonly bool Run;

        public LineTick(
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            IReadOnlyList<Entity> vehicles,
            int nowMinute,
            uint nowFrame,
            IReadOnlyList<int> targets,
            int holdMinutes,
            float lapFrames,
            bool run)
        {
            Line = line;
            Ways = ways;
            Vehicles = vehicles;
            NowMinute = nowMinute;
            NowFrame = nowFrame;
            Targets = targets;
            HoldMinutes = holdMinutes;
            LapFrames = lapFrames;
            Run = run;
        }
    }
}
