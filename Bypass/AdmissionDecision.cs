using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct YieldTradeoffEstimate
    {
        public readonly float LocalExtraWaitFrames;
        public readonly float ExpressCatchEtaFrames;
        public readonly float ExpressReleaseEtaFrames;
        public readonly float LocalNoYieldClearEtaFrames;
        public readonly float ExpressSavedFrames;
        public readonly float SystemCostFrames;
        public readonly float Confidence;

        public YieldTradeoffEstimate(
            float localExtraWaitFrames,
            float expressCatchEtaFrames,
            float expressReleaseEtaFrames,
            float localNoYieldClearEtaFrames,
            float expressSavedFrames,
            float systemCostFrames,
            float confidence)
        {
            LocalExtraWaitFrames = localExtraWaitFrames;
            ExpressCatchEtaFrames = expressCatchEtaFrames;
            ExpressReleaseEtaFrames = expressReleaseEtaFrames;
            LocalNoYieldClearEtaFrames = localNoYieldClearEtaFrames;
            ExpressSavedFrames = expressSavedFrames;
            SystemCostFrames = systemCostFrames;
            Confidence = confidence;
        }
    }

    internal readonly struct ConflictPolicy
    {
        public readonly bool MustYield;
        public readonly bool WorthYielding;

        public ConflictPolicy(bool mustYield, bool worthYielding)
        {
            MustYield = mustYield;
            WorthYielding = worthYielding;
        }
    }

    internal readonly struct BypassTrackModelDecision
    {
        public readonly bool Available;
        public readonly bool ShouldYield;
        public readonly string ReasonCode;
        public readonly int ProtectedIntervalIndex;
        public readonly bool HasReliableLocalPosition;
        public readonly Entity BlockerVehicle;
        public readonly bool UsedFallbackResolution;
        public readonly bool HasLatchedBlockerProjection;
        public readonly BypassLatchedBlockerProjection LatchedBlockerProjection;

        public BypassTrackModelDecision(
            bool available,
            bool shouldYield,
            string reasonCode,
            int protectedIntervalIndex,
            bool hasReliableLocalPosition,
            Entity blockerVehicle,
            bool usedFallbackResolution,
            bool hasLatchedBlockerProjection = false,
            BypassLatchedBlockerProjection latchedBlockerProjection = default)
        {
            Available = available;
            ShouldYield = shouldYield;
            ReasonCode = reasonCode;
            ProtectedIntervalIndex = protectedIntervalIndex;
            HasReliableLocalPosition = hasReliableLocalPosition;
            BlockerVehicle = blockerVehicle;
            UsedFallbackResolution = usedFallbackResolution;
            HasLatchedBlockerProjection = hasLatchedBlockerProjection;
            LatchedBlockerProjection = latchedBlockerProjection;
        }
    }

    internal readonly struct BypassLineExecutionModeSnapshot
    {
        public readonly uint LocalSceneVersion;
        public readonly int SceneCount;
        public readonly int MaxExpressLinesPerScene;
        public readonly int MultiTrunkSceneCount;
        public readonly BypassExecutionMode ExecutionMode;

        public BypassLineExecutionModeSnapshot(
            uint localSceneVersion,
            int sceneCount,
            int maxExpressLinesPerScene,
            int multiTrunkSceneCount,
            BypassExecutionMode executionMode)
        {
            LocalSceneVersion = localSceneVersion;
            SceneCount = sceneCount;
            MaxExpressLinesPerScene = maxExpressLinesPerScene;
            MultiTrunkSceneCount = multiTrunkSceneCount;
            ExecutionMode = executionMode;
        }
    }

    internal enum BypassExecutionMode : byte
    {
        SimpleSceneScan = 0,
        ComplexLineModel = 1,
    }
}
