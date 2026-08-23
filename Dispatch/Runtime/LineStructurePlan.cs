using System;
using RapidTransitMod.TrackModel;
using Unity.Entities;
namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum LineStructurePlanKind : byte
    {
        Precise = 0,
        CrossSaveFallback = 1,
        LineDeleted = 2,
        SafetyFallback = 3,
    }
    internal enum LineTimesPlanKind : byte
    {
        Refresh = 0,
        Rebuild = 1,
        Remove = 2,
    }
    internal enum TimetablePlanKind : byte
    {
        Preserve = 0,
        Reevaluate = 1,
    }
    internal readonly struct LineChainFact
    {
        internal readonly Entity Line;
        internal readonly TransitMode Mode;
        internal readonly ulong Signature;
        internal readonly ulong TraversalSignature;
        internal readonly bool ChainComplete;
        internal readonly int AtomCount;
        internal readonly int SegmentCount;
        internal LineChainFact(LineTrackChain chain)
        {
            Line = chain != null ? chain.LineEntity : Entity.Null;
            Mode = chain != null ? chain.Mode : TransitMode.Unknown;
            Signature = chain != null ? chain.Signature : 0UL;
            TraversalSignature = chain != null ? chain.TraversalSignature : 0UL;
            ChainComplete = chain != null && chain.ChainComplete;
            AtomCount = chain != null && chain.TrackAtoms != null ? chain.TrackAtoms.Count : 0;
            SegmentCount = chain != null && chain.SegmentRanges != null ? chain.SegmentRanges.Count : 0;
        }
        internal bool Matches(LineTrackChain chain)
        {
            return chain != null
                && Line == chain.LineEntity
                && Mode == chain.Mode
                && Signature == chain.Signature
                && TraversalSignature == chain.TraversalSignature
                && ChainComplete == chain.ChainComplete
                && AtomCount == (chain.TrackAtoms != null ? chain.TrackAtoms.Count : 0)
                && SegmentCount == (chain.SegmentRanges != null ? chain.SegmentRanges.Count : 0);
        }
    }
    internal sealed class LineStructurePlan
    {
        private readonly Entity[] m_Vehicles;
        internal readonly Entity Line;
        internal readonly string LineId;
        internal readonly string Mode;
        internal readonly LineStructurePlanKind Kind;
        internal readonly uint CandidateRevision;
        internal readonly LineChainFact OldChainFact;
        internal readonly LineChainFact NewChainFact;
        internal readonly LineStopLayout OldLayout;
        internal readonly LineStopLayout NewLayout;
        internal readonly LineIntervalImpact Impact;
        internal readonly string OldStopSig;
        internal readonly string NewStopSig;
        internal readonly bool StopSigChanged;
        internal readonly LineTimesPlanKind LineTimes;
        internal readonly TimetablePlanKind Timetable;
        internal readonly int AffectedOldIntervalCount;
        internal readonly int AffectedNewIntervalCount;
        internal readonly int RetainedIntervalCount;
        internal int VehicleCount => m_Vehicles.Length;
        internal LineStructurePlan(
            Entity line,
            string lineId,
            string mode,
            LineStructurePlanKind kind,
            uint candidateRevision,
            LineTrackChain oldChain,
            LineTrackChain newChain,
            LineStopLayout oldLayout,
            LineStopLayout newLayout,
            LineIntervalImpact impact,
            string oldStopSig,
            string newStopSig,
            bool stopSigChanged,
            LineTimesPlanKind lineTimes,
            TimetablePlanKind timetable,
            Entity[] vehicles,
            int affectedOldCount = -1,
            int affectedNewCount = -1,
            int retainedCount = -1)
        {
            Line = line;
            LineId = lineId ?? string.Empty;
            Mode = mode ?? string.Empty;
            Kind = kind;
            CandidateRevision = candidateRevision;
            OldChainFact = new LineChainFact(oldChain);
            NewChainFact = new LineChainFact(newChain);
            OldLayout = oldLayout;
            NewLayout = newLayout;
            Impact = impact;
            OldStopSig = oldStopSig ?? string.Empty;
            NewStopSig = newStopSig ?? string.Empty;
            StopSigChanged = stopSigChanged;
            LineTimes = lineTimes;
            Timetable = timetable;
            m_Vehicles = CopyVehicles(vehicles);
            AffectedOldIntervalCount = affectedOldCount >= 0
                ? affectedOldCount
                : (impact != null ? impact.AffectedOldCount : 0);
            AffectedNewIntervalCount = affectedNewCount >= 0
                ? affectedNewCount
                : (impact != null ? impact.AffectedNewCount : 0);
            RetainedIntervalCount = retainedCount >= 0
                ? retainedCount
                : (impact != null ? impact.RetainedCount : 0);
        }
        internal Entity VehicleAt(int index)
        {
            return m_Vehicles[index];
        }
        private static Entity[] CopyVehicles(Entity[] vehicles)
        {
            if (vehicles == null || vehicles.Length == 0)
                return Array.Empty<Entity>();
            Entity[] copy = new Entity[vehicles.Length];
            Array.Copy(vehicles, copy, vehicles.Length);
            return copy;
        }
    }
}
