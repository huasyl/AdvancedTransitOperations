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
        public readonly int Now;
        public readonly uint Frame;
        public readonly IReadOnlyList<int> Targets;
        public readonly int Hold;
        public readonly float Lap;
        public readonly bool Run;

        public LineTick(
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            IReadOnlyList<Entity> vehicles,
            int now,
            uint frame,
            IReadOnlyList<int> targets,
            int hold,
            float lap,
            bool run)
        {
            Line = line;
            Ways = ways;
            Vehicles = vehicles;
            Now = now;
            Frame = frame;
            Targets = targets;
            Hold = hold;
            Lap = lap;
            Run = run;
        }
    }
}
