using System.Collections.Generic;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct SceneExpressRelation
    {
        public readonly Entity ExpressLine;
        public readonly LineTrackChain ExpressChain;
        public readonly bool Ambiguous;
        public readonly int ExpressProtectedIntervalIndex;
        public readonly BypassProtectedInterval ExpressProtectedInterval;
        public readonly int OverlapCount;
        public readonly int OrderedRun;
        public readonly string ResolutionSource;
        public readonly bool HasRelevantSharedEntryAtomIndex;
        public readonly int RelevantSharedEntryAtomIndex;
        public readonly bool HasSelectedTrunkSegment;
        public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
        public readonly TrunkSkeleton TrunkSkeleton;
        public readonly SceneRelationTrunkCandidateSet TrunkCandidates;

        public SceneExpressRelation(
            Entity expressLine,
            LineTrackChain expressChain,
            bool ambiguous,
            int expressProtectedIntervalIndex,
            BypassProtectedInterval expressProtectedInterval,
            int overlapCount,
            int orderedRun,
            string resolutionSource,
            bool hasRelevantSharedEntryAtomIndex,
            int relevantSharedEntryAtomIndex,
            bool hasSelectedTrunkSegment,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            TrunkSkeleton trunkSkeleton,
            SceneRelationTrunkCandidateSet trunkCandidates)
        {
            ExpressLine = expressLine;
            ExpressChain = expressChain;
            Ambiguous = ambiguous;
            ExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
            ExpressProtectedInterval = expressProtectedInterval;
            OverlapCount = overlapCount;
            OrderedRun = orderedRun;
            ResolutionSource = resolutionSource;
            HasRelevantSharedEntryAtomIndex = hasRelevantSharedEntryAtomIndex;
            RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
            HasSelectedTrunkSegment = hasSelectedTrunkSegment;
            SelectedTrunkSegment = selectedTrunkSegment;
            TrunkSkeleton = trunkSkeleton;
            TrunkCandidates = trunkCandidates;
        }
    }

    internal sealed class SceneRelationTrunkCandidateSet
    {
        public readonly List<GlobalSharedTrunkSegment> Segments = new List<GlobalSharedTrunkSegment>();
    }

    internal readonly struct SceneExpressVehicleCandidate
    {
        public readonly SceneExpressRelation Relation;
        public readonly Entity ExpressLine;
        public readonly Entity ExpressVehicle;
        public readonly LineTrackChain ExpressChain;
        public readonly LineRunningVehicleSnapshot RunningVehicle;
        public readonly int ExpressProtectedIntervalIndex;
        public readonly BypassProtectedInterval ExpressProtectedInterval;
        public readonly int OverlapCount;
        public readonly int OrderedRun;
        public readonly string IntervalResolutionSource;
        public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
        public readonly TrunkSkeleton TrunkSkeleton;
        public readonly RelativeToTrunkState LocalTrunkState;
        public readonly RelativeToTrunkState ExpressTrunkState;
        public readonly int RelevantSharedEntryAtomIndex;
        public readonly int EntryDistanceAtoms;

        public SceneExpressVehicleCandidate(
            SceneExpressRelation relation,
            Entity expressLine,
            Entity expressVehicle,
            LineTrackChain expressChain,
            LineRunningVehicleSnapshot runningVehicle,
            int expressProtectedIntervalIndex,
            BypassProtectedInterval expressProtectedInterval,
            int overlapCount,
            int orderedRun,
            string intervalResolutionSource,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            TrunkSkeleton trunkSkeleton,
            RelativeToTrunkState localTrunkState,
            RelativeToTrunkState expressTrunkState,
            int relevantSharedEntryAtomIndex,
            int entryDistanceAtoms)
        {
            Relation = relation;
            ExpressLine = expressLine;
            ExpressVehicle = expressVehicle;
            ExpressChain = expressChain;
            RunningVehicle = runningVehicle;
            ExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
            ExpressProtectedInterval = expressProtectedInterval;
            OverlapCount = overlapCount;
            OrderedRun = orderedRun;
            IntervalResolutionSource = intervalResolutionSource;
            SelectedTrunkSegment = selectedTrunkSegment;
            TrunkSkeleton = trunkSkeleton;
            LocalTrunkState = localTrunkState;
            ExpressTrunkState = expressTrunkState;
            RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
            EntryDistanceAtoms = entryDistanceAtoms;
        }
    }

    internal readonly struct SceneExpressFrontier
    {
        public readonly SceneExpressRelation Relation;
        public readonly bool HasPrimaryCandidate;
        public readonly SceneExpressVehicleCandidate PrimaryCandidate;
        public readonly bool HasSecondaryCandidate;
        public readonly SceneExpressVehicleCandidate SecondaryCandidate;
        public readonly int AdmittedCandidateCount;

        public SceneExpressFrontier(
            SceneExpressRelation relation,
            bool hasPrimaryCandidate,
            SceneExpressVehicleCandidate primaryCandidate,
            bool hasSecondaryCandidate,
            SceneExpressVehicleCandidate secondaryCandidate,
            int admittedCandidateCount)
        {
            Relation = relation;
            HasPrimaryCandidate = hasPrimaryCandidate;
            PrimaryCandidate = primaryCandidate;
            HasSecondaryCandidate = hasSecondaryCandidate;
            SecondaryCandidate = secondaryCandidate;
            AdmittedCandidateCount = admittedCandidateCount;
        }
    }

    internal sealed class SceneExpressFrontierAccumulator
    {
        public readonly SceneExpressRelation Relation;
        public bool HasPrimaryCandidate;
        public SceneExpressVehicleCandidate PrimaryCandidate;
        public bool HasSecondaryCandidate;
        public SceneExpressVehicleCandidate SecondaryCandidate;
        public int AdmittedCandidateCount;

        public SceneExpressFrontierAccumulator(SceneExpressRelation relation)
        {
            Relation = relation;
        }
    }
}
