using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal static class Keys
    {
        internal static ulong Slice(Entity line, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)sliceIndex;
            }
        }

        internal static ulong SliceDebug(Entity vehicle, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)vehicle.Index << 32) | (uint)sliceIndex;
            }
        }

        internal static ulong WaypointDwell(Entity line, int waypointIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)math.max(0, waypointIndex);
            }
        }
    }
}
