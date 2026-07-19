namespace RapidTransitMod
{
    internal readonly struct ReadyClockState
    {
        public ReadyClockState(uint startFrame, double waitMinutes, uint readyFrame)
        {
            StartFrame = startFrame;
            WaitMinutes = waitMinutes;
            ReadyFrame = readyFrame;
        }

        public uint StartFrame { get; }

        public double WaitMinutes { get; }

        public uint ReadyFrame { get; }
    }
}
