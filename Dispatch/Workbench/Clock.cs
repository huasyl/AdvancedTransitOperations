using System;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Clock
    {
        private readonly Func<int> m_Now;

        internal Clock(Func<int> now)
        {
            m_Now = now ?? throw new ArgumentNullException(nameof(now));
        }

        internal string Now()
        {
            return Time.Format(m_Now());
        }

        internal void Window(DispatchWorkbenchMergedView view)
        {
            if (view == null)
                return;

            if (!string.IsNullOrEmpty(view.windowStart) && !string.IsNullOrEmpty(view.windowEnd))
                return;

            int currentMinutes = m_Now();
            int startMinutes = (currentMinutes / 30) * 30;
            int endMinutes = startMinutes + 90;
            if (endMinutes > 1439)
            {
                startMinutes = 22 * 60 + 30;
                endMinutes = 23 * 60 + 59;
            }

            view.windowStart = string.IsNullOrEmpty(view.windowStart)
                ? Time.Format(startMinutes)
                : view.windowStart;
            view.windowEnd = string.IsNullOrEmpty(view.windowEnd)
                ? Time.Format(endMinutes)
                : view.windowEnd;
        }
    }
}
