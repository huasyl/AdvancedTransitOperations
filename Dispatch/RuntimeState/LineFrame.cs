using System;
using RapidTransitMod.Dispatch;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct LineFrame
    {
        public readonly Entity Line;
        public readonly uint Frame;
        public readonly string Id;
        public readonly string Key;
        public readonly bool Applied;
        public readonly string CfgKind;
        public readonly string AppliedKind;
        public readonly string Kind;

        public LineFrame(
            Entity line,
            uint frame,
            string id,
            string key,
            bool applied,
            string cfgKind,
            string appliedKind,
            string kind)
        {
            Line = line;
            Frame = frame;
            Id = id ?? string.Empty;
            Key = key ?? string.Empty;
            Applied = applied;
            CfgKind = cfgKind ?? string.Empty;
            AppliedKind = appliedKind ?? string.Empty;
            Kind = kind ?? string.Empty;
        }
    }

    internal readonly struct LineInfo
    {
        public readonly Entity Line;
        public readonly string Id;
        public readonly string Key;
        public readonly LineKey StoreKey;
        public readonly bool Applied;
        public readonly int[] Times;
        public readonly string CfgKind;
        public readonly string AppliedKind;
        public readonly string Kind;
        public readonly int Hold;
        public readonly int Dwell;
        public readonly string DepotId;

        public LineInfo(
            Entity line,
            string id,
            string key,
            LineKey storeKey,
            bool applied,
            int[] times,
            string cfgKind,
            string appliedKind,
            string kind,
            int hold,
            int dwell,
            string depotId)
        {
            Line = line;
            Id = id ?? string.Empty;
            Key = key ?? string.Empty;
            StoreKey = storeKey;
            Applied = applied;
            Times = times ?? Array.Empty<int>();
            CfgKind = cfgKind ?? string.Empty;
            AppliedKind = appliedKind ?? string.Empty;
            Kind = kind ?? string.Empty;
            Hold = hold;
            Dwell = dwell;
            DepotId = depotId ?? string.Empty;
        }
    }
}
