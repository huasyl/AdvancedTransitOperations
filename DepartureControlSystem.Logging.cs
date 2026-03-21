using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private readonly Dictionary<Entity, string> m_YieldSkipLogCache = new Dictionary<Entity, string>();

        private void ClearDispatchLogCaches()
        {
            m_BypassDecisionLogCache.Clear();
            m_PreparingSlotLogCache.Clear();
            m_HoldingSkipLogCache.Clear();
            m_LateDispatchLogCache.Clear();
            m_YieldSkipLogCache.Clear();
        }
    }
}
