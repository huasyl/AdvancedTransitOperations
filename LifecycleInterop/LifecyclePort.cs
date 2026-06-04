using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LifecyclePort
    {
        public static LifecyclePort Current { get; private set; } = null!;

        public ManagedRequestPort ManagedRequests { get; }
        public RetireGuardPort RetireGuard { get; }
        public OriginRepairPort OriginRepair { get; }

        public LifecyclePort(
            ManagedRequestPort managedRequests,
            RetireGuardPort retireGuard,
            OriginRepairPort originRepair)
        {
            ManagedRequests = managedRequests;
            RetireGuard = retireGuard;
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
