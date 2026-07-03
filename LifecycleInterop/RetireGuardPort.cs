namespace RapidTransitMod
{
    internal delegate void GuardRetireDelegate(uint nowFrame);

    internal sealed class RetireGuardPort
    {
        private readonly GuardRetireDelegate m_Guard;

        public RetireGuardPort(GuardRetireDelegate guard)
        {
            m_Guard = guard;
        }

        public void Guard(uint nowFrame)
        {
            m_Guard(nowFrame);
        }
    }
}
