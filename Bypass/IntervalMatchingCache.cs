using System;
using System.Collections.Generic;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct SharedWindowMatchCacheKey : IEquatable<SharedWindowMatchCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity ExpressLine;
        public readonly Entity CurrentBypassBuilding;
        public readonly int LocalStartAtomIndex;
        public readonly int LocalEndAtomIndexExclusive;

        public SharedWindowMatchCacheKey(
            Entity localLine,
            Entity expressLine,
            Entity currentBypassBuilding,
            int localStartAtomIndex,
            int localEndAtomIndexExclusive)
        {
            LocalLine = localLine;
            ExpressLine = expressLine;
            CurrentBypassBuilding = currentBypassBuilding;
            LocalStartAtomIndex = localStartAtomIndex;
            LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
        }

        public bool Equals(SharedWindowMatchCacheKey other)
        {
            return LocalLine == other.LocalLine
                && ExpressLine == other.ExpressLine
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && LocalStartAtomIndex == other.LocalStartAtomIndex
                && LocalEndAtomIndexExclusive == other.LocalEndAtomIndexExclusive;
        }

        public override bool Equals(object obj)
        {
            return obj is SharedWindowMatchCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalLine.GetHashCode();
                hash = (hash * 397) ^ ExpressLine.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ LocalStartAtomIndex;
                hash = (hash * 397) ^ LocalEndAtomIndexExclusive;
                return hash;
            }
        }
    }

    internal readonly struct SharedWindowMatchSnapshot
    {
        public readonly uint SharedTrackVersion;
        public readonly ulong LocalChainSignature;
        public readonly ulong ExpressChainSignature;
        public readonly PhysicalSharedWindowMatch Match;

        public SharedWindowMatchSnapshot(
            uint sharedTrackVersion,
            ulong localChainSignature,
            ulong expressChainSignature,
            PhysicalSharedWindowMatch match)
        {
            SharedTrackVersion = sharedTrackVersion;
            LocalChainSignature = localChainSignature;
            ExpressChainSignature = expressChainSignature;
            Match = match;
        }
    }

    internal readonly struct LocalBypassSceneStaticKey : IEquatable<LocalBypassSceneStaticKey>
    {
        public readonly Entity Line;
        public readonly int WaypointIndex;

        public LocalBypassSceneStaticKey(Entity line, int waypointIndex)
        {
            Line = line;
            WaypointIndex = waypointIndex;
        }

        public bool Equals(LocalBypassSceneStaticKey other)
        {
            return Line == other.Line
                && WaypointIndex == other.WaypointIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is LocalBypassSceneStaticKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Line.GetHashCode() * 397) ^ WaypointIndex;
            }
        }
    }

    internal readonly struct LocalBypassSceneStaticSnapshot
    {
        public readonly SceneKey SceneKey;
        public readonly ulong LineChainSignature;
        public readonly Entity CurrentBypassBuilding;
        public readonly Entity NextBypassBuilding;
        public readonly int ProtectedIntervalIndex;
        public readonly BypassProtectedInterval ProtectedInterval;
        public readonly ProtectedIntervalSummary Summary;
        public readonly float DepartureReleaseCoordinate;
        public readonly float IntervalDisplayLength;

        public LocalBypassSceneStaticSnapshot(
            SceneKey sceneKey,
            ulong lineChainSignature,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            ProtectedIntervalSummary summary,
            float departureReleaseCoordinate,
            float intervalDisplayLength)
        {
            SceneKey = sceneKey;
            LineChainSignature = lineChainSignature;
            CurrentBypassBuilding = currentBypassBuilding;
            NextBypassBuilding = nextBypassBuilding;
            ProtectedIntervalIndex = protectedIntervalIndex;
            ProtectedInterval = protectedInterval;
            Summary = summary;
            DepartureReleaseCoordinate = departureReleaseCoordinate;
            IntervalDisplayLength = intervalDisplayLength;
        }
    }

    internal readonly struct LocalSceneCandidateExpressLinesCacheKey : IEquatable<LocalSceneCandidateExpressLinesCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity CurrentBypassBuilding;
        public readonly int LocalProtectedIntervalIndex;

        public LocalSceneCandidateExpressLinesCacheKey(
            Entity localLine,
            Entity currentBypassBuilding,
            int localProtectedIntervalIndex)
        {
            LocalLine = localLine;
            CurrentBypassBuilding = currentBypassBuilding;
            LocalProtectedIntervalIndex = localProtectedIntervalIndex;
        }

        public bool Equals(LocalSceneCandidateExpressLinesCacheKey other)
        {
            return LocalLine == other.LocalLine
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && LocalProtectedIntervalIndex == other.LocalProtectedIntervalIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is LocalSceneCandidateExpressLinesCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalLine.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ LocalProtectedIntervalIndex;
                return hash;
            }
        }
    }

    internal sealed class LocalSceneCandidateExpressLinesSnapshot
    {
        public uint SharedTrackVersion;
        public ulong LocalChainSignature;
        public readonly List<Entity> ExpressLines = new List<Entity>();
    }

    internal readonly struct LocalSceneExpressStaticMatchCacheKey : IEquatable<LocalSceneExpressStaticMatchCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity ExpressLine;
        public readonly Entity CurrentBypassBuilding;
        public readonly int LocalProtectedIntervalIndex;

        public LocalSceneExpressStaticMatchCacheKey(
            Entity localLine,
            Entity expressLine,
            Entity currentBypassBuilding,
            int localProtectedIntervalIndex)
        {
            LocalLine = localLine;
            ExpressLine = expressLine;
            CurrentBypassBuilding = currentBypassBuilding;
            LocalProtectedIntervalIndex = localProtectedIntervalIndex;
        }

        public bool Equals(LocalSceneExpressStaticMatchCacheKey other)
        {
            return LocalLine == other.LocalLine
                && ExpressLine == other.ExpressLine
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && LocalProtectedIntervalIndex == other.LocalProtectedIntervalIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is LocalSceneExpressStaticMatchCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalLine.GetHashCode();
                hash = (hash * 397) ^ ExpressLine.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ LocalProtectedIntervalIndex;
                return hash;
            }
        }
    }

    internal readonly struct LocalSceneExpressStaticMatchSnapshot
    {
        public readonly uint SharedTrackVersion;
        public readonly ulong LocalChainSignature;
        public readonly ulong ExpressChainSignature;
        public readonly bool Found;
        public readonly bool Ambiguous;
        public readonly BypassProtectedInterval LocalSharedWindow;
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

        public LocalSceneExpressStaticMatchSnapshot(
            uint sharedTrackVersion,
            ulong localChainSignature,
            ulong expressChainSignature,
            bool found,
            bool ambiguous,
            BypassProtectedInterval localSharedWindow,
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
            SharedTrackVersion = sharedTrackVersion;
            LocalChainSignature = localChainSignature;
            ExpressChainSignature = expressChainSignature;
            Found = found;
            Ambiguous = ambiguous;
            LocalSharedWindow = localSharedWindow;
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

    internal readonly struct GlobalSharedTrunkCacheKey : IEquatable<GlobalSharedTrunkCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity ExpressLine;

        public GlobalSharedTrunkCacheKey(Entity localLine, Entity expressLine)
        {
            LocalLine = localLine;
            ExpressLine = expressLine;
        }

        public bool Equals(GlobalSharedTrunkCacheKey other)
        {
            return LocalLine == other.LocalLine
                && ExpressLine == other.ExpressLine;
        }

        public override bool Equals(object obj)
        {
            return obj is GlobalSharedTrunkCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (LocalLine.GetHashCode() * 397) ^ ExpressLine.GetHashCode();
            }
        }
    }

    internal sealed class GlobalSharedTrunkSnapshot
    {
        public uint SharedTrackVersion;
        public ulong LocalChainSignature;
        public ulong ExpressChainSignature;
        public readonly List<GlobalSharedTrunkSegment> Segments = new List<GlobalSharedTrunkSegment>();
    }

    internal readonly struct ProtectedIntervalPairMetricsCacheKey : IEquatable<ProtectedIntervalPairMetricsCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity ExpressLine;

        public ProtectedIntervalPairMetricsCacheKey(Entity localLine, Entity expressLine)
        {
            LocalLine = localLine;
            ExpressLine = expressLine;
        }

        public bool Equals(ProtectedIntervalPairMetricsCacheKey other)
        {
            return LocalLine == other.LocalLine
                && ExpressLine == other.ExpressLine;
        }

        public override bool Equals(object obj)
        {
            return obj is ProtectedIntervalPairMetricsCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (LocalLine.GetHashCode() * 397) ^ ExpressLine.GetHashCode();
            }
        }
    }

    internal readonly struct ProtectedIntervalPairMetrics
    {
        public readonly int OverlapCount;
        public readonly int OrderedRun;

        public ProtectedIntervalPairMetrics(int overlapCount, int orderedRun)
        {
            OverlapCount = overlapCount;
            OrderedRun = orderedRun;
        }
    }

    internal sealed class ProtectedIntervalPairMetricsSnapshot
    {
        public uint SharedTrackVersion;
        public ulong LocalChainSignature;
        public ulong ExpressChainSignature;
        public int LocalIntervalCount;
        public int ExpressIntervalCount;
        public ProtectedIntervalPairMetrics[] Metrics = Array.Empty<ProtectedIntervalPairMetrics>();
    }

    internal readonly struct ActiveConflictCorridorCacheKey : IEquatable<ActiveConflictCorridorCacheKey>
    {
        public readonly Entity LocalLine;
        public readonly Entity ExpressLine;
        public readonly Entity CurrentBypassBuilding;
        public readonly int LocalStartControlPointIndex;
        public readonly int LocalEndControlPointIndex;
        public readonly int ExpressStartControlPointIndex;
        public readonly int ExpressEndControlPointIndex;
        public readonly int LocalStartAtomIndex;
        public readonly int LocalEndAtomIndexExclusive;
        public readonly int ExpressStartAtomIndex;
        public readonly int ExpressEndAtomIndexExclusive;
        public readonly bool HasPreselectedTrunkSegment;
        public readonly GlobalSharedTrunkSegment PreselectedTrunkSegment;
        public readonly int ExpressCurrentAtomIndex;

        public ActiveConflictCorridorCacheKey(
            Entity localLine,
            Entity expressLine,
            Entity currentBypassBuilding,
            int localStartControlPointIndex,
            int localEndControlPointIndex,
            int expressStartControlPointIndex,
            int expressEndControlPointIndex,
            int localStartAtomIndex,
            int localEndAtomIndexExclusive,
            int expressStartAtomIndex,
            int expressEndAtomIndexExclusive,
            bool hasPreselectedTrunkSegment,
            GlobalSharedTrunkSegment preselectedTrunkSegment,
            int expressCurrentAtomIndex)
        {
            LocalLine = localLine;
            ExpressLine = expressLine;
            CurrentBypassBuilding = currentBypassBuilding;
            LocalStartControlPointIndex = localStartControlPointIndex;
            LocalEndControlPointIndex = localEndControlPointIndex;
            ExpressStartControlPointIndex = expressStartControlPointIndex;
            ExpressEndControlPointIndex = expressEndControlPointIndex;
            LocalStartAtomIndex = localStartAtomIndex;
            LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
            ExpressStartAtomIndex = expressStartAtomIndex;
            ExpressEndAtomIndexExclusive = expressEndAtomIndexExclusive;
            HasPreselectedTrunkSegment = hasPreselectedTrunkSegment;
            PreselectedTrunkSegment = preselectedTrunkSegment;
            ExpressCurrentAtomIndex = expressCurrentAtomIndex;
        }

        public bool Equals(ActiveConflictCorridorCacheKey other)
        {
            return LocalLine == other.LocalLine
                && ExpressLine == other.ExpressLine
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && LocalStartControlPointIndex == other.LocalStartControlPointIndex
                && LocalEndControlPointIndex == other.LocalEndControlPointIndex
                && ExpressStartControlPointIndex == other.ExpressStartControlPointIndex
                && ExpressEndControlPointIndex == other.ExpressEndControlPointIndex
                && LocalStartAtomIndex == other.LocalStartAtomIndex
                && LocalEndAtomIndexExclusive == other.LocalEndAtomIndexExclusive
                && ExpressStartAtomIndex == other.ExpressStartAtomIndex
                && ExpressEndAtomIndexExclusive == other.ExpressEndAtomIndexExclusive
                && HasPreselectedTrunkSegment == other.HasPreselectedTrunkSegment
                && PreselectedTrunkSegment.Equals(other.PreselectedTrunkSegment)
                && ExpressCurrentAtomIndex == other.ExpressCurrentAtomIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is ActiveConflictCorridorCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalLine.GetHashCode();
                hash = (hash * 397) ^ ExpressLine.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ LocalStartControlPointIndex;
                hash = (hash * 397) ^ LocalEndControlPointIndex;
                hash = (hash * 397) ^ ExpressStartControlPointIndex;
                hash = (hash * 397) ^ ExpressEndControlPointIndex;
                hash = (hash * 397) ^ LocalStartAtomIndex;
                hash = (hash * 397) ^ LocalEndAtomIndexExclusive;
                hash = (hash * 397) ^ ExpressStartAtomIndex;
                hash = (hash * 397) ^ ExpressEndAtomIndexExclusive;
                hash = (hash * 397) ^ HasPreselectedTrunkSegment.GetHashCode();
                hash = (hash * 397) ^ PreselectedTrunkSegment.GetHashCode();
                hash = (hash * 397) ^ ExpressCurrentAtomIndex;
                return hash;
            }
        }
    }

    internal readonly struct SharedWindowPairStateKey : IEquatable<SharedWindowPairStateKey>
    {
        public readonly Entity LocalVehicle;
        public readonly int ProtectedIntervalIndex;
        public readonly Entity ExpressVehicle;

        public SharedWindowPairStateKey(Entity localVehicle, int protectedIntervalIndex, Entity expressVehicle)
        {
            LocalVehicle = localVehicle;
            ProtectedIntervalIndex = protectedIntervalIndex;
            ExpressVehicle = expressVehicle;
        }

        public bool Equals(SharedWindowPairStateKey other)
        {
            return LocalVehicle == other.LocalVehicle
                && ProtectedIntervalIndex == other.ProtectedIntervalIndex
                && ExpressVehicle == other.ExpressVehicle;
        }

        public override bool Equals(object obj)
        {
            return obj is SharedWindowPairStateKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalVehicle.GetHashCode();
                hash = (hash * 397) ^ ProtectedIntervalIndex;
                hash = (hash * 397) ^ ExpressVehicle.GetHashCode();
                return hash;
            }
        }
    }

    internal readonly struct ConflictCorridor
    {
        public readonly int ProtectedIntervalIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int AnchorSharedStartAtomIndex;
        public readonly int AnchorSharedEndAtomIndexExclusive;
        public readonly int SharedSliceCount;
        public readonly int BridgedGapAtoms;

        public ConflictCorridor(
            int protectedIntervalIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int anchorSharedStartAtomIndex,
            int anchorSharedEndAtomIndexExclusive,
            int sharedSliceCount,
            int bridgedGapAtoms)
        {
            ProtectedIntervalIndex = protectedIntervalIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            AnchorSharedStartAtomIndex = anchorSharedStartAtomIndex;
            AnchorSharedEndAtomIndexExclusive = anchorSharedEndAtomIndexExclusive;
            SharedSliceCount = sharedSliceCount;
            BridgedGapAtoms = bridgedGapAtoms;
        }
    }

    internal readonly struct ActiveConflictCorridorSnapshot
    {
        public readonly uint Frame;
        public readonly uint SharedTrackVersion;
        public readonly ulong LocalChainSignature;
        public readonly ulong ExpressChainSignature;
        public readonly bool Available;
        public readonly ConflictCorridor LocalCorridor;
        public readonly ConflictCorridor ExpressCorridor;
        public readonly GlobalSharedTrunkSegment TrunkSegment;

        public ActiveConflictCorridorSnapshot(
            uint frame,
            uint sharedTrackVersion,
            ulong localChainSignature,
            ulong expressChainSignature,
            bool available,
            ConflictCorridor localCorridor,
            ConflictCorridor expressCorridor,
            GlobalSharedTrunkSegment trunkSegment)
        {
            Frame = frame;
            SharedTrackVersion = sharedTrackVersion;
            LocalChainSignature = localChainSignature;
            ExpressChainSignature = expressChainSignature;
            Available = available;
            LocalCorridor = localCorridor;
            ExpressCorridor = expressCorridor;
            TrunkSegment = trunkSegment;
        }
    }
}
