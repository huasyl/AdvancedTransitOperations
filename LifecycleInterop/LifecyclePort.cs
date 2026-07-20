using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LifecyclePort
    {
        public static LifecyclePort Current { get; private set; } = null!;

        public ManagedRequestPort ManagedRequests { get; }
        public OriginRepairPort OriginRepair { get; }

        public LifecyclePort(
            ManagedRequestPort managedRequests,
            OriginRepairPort originRepair)
        {
            ManagedRequests = managedRequests;
            OriginRepair = originRepair;
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
