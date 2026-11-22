using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LifecyclePort
    {
        public static LifecyclePort Current { get; private set; } = null!;

        public ManagedRequestPort ManagedRequests { get; }
#if RT_DEBUG_TOOLS
        public OriginRepairPort OriginRepair { get; }
#endif

        public LifecyclePort(
#if RT_DEBUG_TOOLS
            OriginRepairPort originRepair,
#endif
            ManagedRequestPort managedRequests)
        {
            ManagedRequests = managedRequests;
#if RT_DEBUG_TOOLS
            OriginRepair = originRepair;
#endif
        }

        public static void Bind(LifecyclePort port)
        {
            Current = port;
        }

        public static void Clear()
        {
            Current = null!;
        }
    }
}
