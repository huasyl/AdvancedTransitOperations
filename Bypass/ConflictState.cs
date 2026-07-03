using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal enum BypassConflictMode : byte
    {
        Unknown = 0,
        Block = 1,
        EtaRefresh = 2,
    }

    internal readonly struct BypassConflictEpisode
    {
        public readonly Entity LocalVehicle;
        public readonly SceneKey SceneKey;
        public readonly Entity ExpressLine;
        public readonly Entity BlockerVehicle;
        public readonly BypassConflictMode Mode;
        public readonly uint AcquiredFrame;
        public readonly uint LastQueuedLocalReleaseCheckFrame;
        public readonly uint LastReleaseCheckFrame;
        public readonly bool LastReleaseCheckBeforeRelease;
        public readonly bool CanClearAfterExit;
        public readonly bool SameStationRequired;
        public readonly bool HasLatchedBlockerProjection;
        public readonly BypassLatchedBlockerProjection LatchedBlockerProjection;

        public BypassConflictEpisode(
            Entity localVehicle,
            SceneKey sceneKey,
            Entity expressLine,
            Entity blockerVehicle,
            BypassConflictMode mode,
            uint acquiredFrame,
            uint lastQueuedLocalReleaseCheckFrame,
            uint lastReleaseCheckFrame,
            bool lastReleaseCheckBeforeRelease,
            bool canClearAfterExit,
            bool sameStationRequired,
            bool hasLatchedBlockerProjection = false,
            BypassLatchedBlockerProjection latchedBlockerProjection = default)
        {
            LocalVehicle = localVehicle;
            SceneKey = sceneKey;
            ExpressLine = expressLine;
            BlockerVehicle = blockerVehicle;
            Mode = mode;
            AcquiredFrame = acquiredFrame;
            LastQueuedLocalReleaseCheckFrame = lastQueuedLocalReleaseCheckFrame;
            LastReleaseCheckFrame = lastReleaseCheckFrame;
            LastReleaseCheckBeforeRelease = lastReleaseCheckBeforeRelease;
            CanClearAfterExit = canClearAfterExit;
            SameStationRequired = sameStationRequired;
            HasLatchedBlockerProjection = hasLatchedBlockerProjection;
            LatchedBlockerProjection = latchedBlockerProjection;
        }
    }

    internal readonly struct BypassLatchedBlockerProjection
    {
        public readonly bool Available;
        public readonly Entity ExpressLine;
        public readonly BypassProtectedInterval ExpressProtectedInterval;
        public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
        public readonly ulong ExpressChainSignature;
        public readonly uint SharedTrackVersion;
        public readonly float ExpressReleaseCoordinate;

        public BypassLatchedBlockerProjection(
            Entity expressLine,
            BypassProtectedInterval expressProtectedInterval,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            ulong expressChainSignature,
            uint sharedTrackVersion,
            float expressReleaseCoordinate)
        {
            Available = expressLine != Entity.Null;
            ExpressLine = expressLine;
            ExpressProtectedInterval = expressProtectedInterval;
            SelectedTrunkSegment = selectedTrunkSegment;
            ExpressChainSignature = expressChainSignature;
            SharedTrackVersion = sharedTrackVersion;
            ExpressReleaseCoordinate = expressReleaseCoordinate;
        }
    }
}
