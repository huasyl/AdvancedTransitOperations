using System;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class SliceAdmissionPort
    {
        public Func<Entity, (bool Success, LineKey Key)> StableKey = null!;
        public Func<DateTime> ServiceDate = null!;
        public Func<Entity, int[]> DepartureMinutes = null!;
        public Func<Entity, (bool Success, ulong Signature)> ProfileSignature = null!;
        public Func<int, string> FormatMinute = null!;
        public Func<LineKey, TraversalSliceDailyQuota, bool> TryFlushDailyQuota = null!;
        public Func<LineKey, TraversalSliceColdStart, bool> TryFlushColdStart = null!;
        public Action<LineKey> RemoveColdStart = null!;
        public Action<string> Log = null!;
    }
}
