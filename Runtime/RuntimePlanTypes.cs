using System;
using Unity.Entities;

namespace RapidTransitMod.Runtime
{
    [Flags]
    internal enum RuntimeStageMask : byte
    {
        None = 0,
        Stop = 1 << 0,
        Bypass = 1 << 1,
        Dispatch = 1 << 2,
        Retire = 1 << 3,
        Rescue = 1 << 4,
        Slice = 1 << 5
    }

    [Flags]
    internal enum RuntimeDemandMask : byte
    {
        None = 0,
        DeparturePending = 1 << 0,
        BypassWatch = 1 << 1,
        BypassActive = 1 << 2,
        OriginCandidate = 1 << 3,
        InboundWatch = 1 << 4
    }

    internal readonly struct FramePlanEntry
    {
        public readonly Entity Vehicle;
        public readonly int SourceRowIndex;
        public readonly RuntimeStageMask Stages;

        public FramePlanEntry(Entity vehicle, int sourceRowIndex, RuntimeStageMask stages)
        {
            Vehicle = vehicle;
            SourceRowIndex = sourceRowIndex;
            Stages = stages;
        }
    }
}
