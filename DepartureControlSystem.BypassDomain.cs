using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private enum BypassConflictMode : byte
        {
            Unknown = 0,
            Block = 1,
            EtaRefresh = 2,
        }

        private readonly struct SceneKey : IEquatable<SceneKey>
        {
            public readonly Entity Line;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly int ProtectedIntervalIndex;

            public SceneKey(
                Entity line,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                int protectedIntervalIndex)
            {
                Line = line;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                ProtectedIntervalIndex = protectedIntervalIndex;
            }

            public bool Equals(SceneKey other)
            {
                return Line == other.Line
                    && CurrentBypassBuilding == other.CurrentBypassBuilding
                    && NextBypassBuilding == other.NextBypassBuilding
                    && ProtectedIntervalIndex == other.ProtectedIntervalIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is SceneKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Line.GetHashCode();
                    hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ NextBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ ProtectedIntervalIndex;
                    return hash;
                }
            }
        }

        private readonly struct SceneDefinition
        {
            public readonly SceneKey Key;
            public readonly Entity Line;
            public readonly int WaypointIndex;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly int ProtectedIntervalIndex;
            public readonly BypassProtectedInterval ProtectedInterval;
            public readonly ProtectedIntervalSummary Summary;
            public readonly float DepartureReleaseCoordinate;
            public readonly float IntervalDisplayLength;

            public SceneDefinition(
                SceneKey key,
                Entity line,
                int waypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                int protectedIntervalIndex,
                BypassProtectedInterval protectedInterval,
                ProtectedIntervalSummary summary,
                float departureReleaseCoordinate,
                float intervalDisplayLength)
            {
                Key = key;
                Line = line;
                WaypointIndex = waypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                ProtectedIntervalIndex = protectedIntervalIndex;
                ProtectedInterval = protectedInterval;
                Summary = summary;
                DepartureReleaseCoordinate = departureReleaseCoordinate;
                IntervalDisplayLength = intervalDisplayLength;
            }
        }

        private readonly struct VehicleSceneBinding
        {
            public readonly Entity Vehicle;
            public readonly SceneKey SceneKey;
            public readonly int WaypointIndex;

            public VehicleSceneBinding(Entity vehicle, SceneKey sceneKey, int waypointIndex)
            {
                Vehicle = vehicle;
                SceneKey = sceneKey;
                WaypointIndex = waypointIndex;
            }
        }

        private readonly struct BypassConflictEpisode
        {
            public readonly Entity LocalVehicle;
            public readonly SceneKey SceneKey;
            public readonly Entity ExpressLine;
            public readonly Entity BlockerVehicle;
            public readonly BypassConflictMode Mode;
            public readonly uint AcquiredFrame;
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
                CanClearAfterExit = canClearAfterExit;
                SameStationRequired = sameStationRequired;
                HasLatchedBlockerProjection = hasLatchedBlockerProjection;
                LatchedBlockerProjection = latchedBlockerProjection;
            }
        }

        private readonly struct BypassLatchedBlockerProjection
        {
            public readonly bool Available;
            public readonly Entity ExpressLine;
            public readonly BypassProtectedInterval ExpressProtectedInterval;
            public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
            public readonly ulong ExpressChainSignature;
            public readonly uint SharedTrackVersion;

            public BypassLatchedBlockerProjection(
                Entity expressLine,
                BypassProtectedInterval expressProtectedInterval,
                GlobalSharedTrunkSegment selectedTrunkSegment,
                ulong expressChainSignature,
                uint sharedTrackVersion)
            {
                Available = expressLine != Entity.Null;
                ExpressLine = expressLine;
                ExpressProtectedInterval = expressProtectedInterval;
                SelectedTrunkSegment = selectedTrunkSegment;
                ExpressChainSignature = expressChainSignature;
                SharedTrackVersion = sharedTrackVersion;
            }
        }

        private readonly struct YieldTradeoffEstimate
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

        private readonly struct ConflictPolicy
        {
            public readonly bool MustYield;
            public readonly bool WorthYielding;

            public ConflictPolicy(bool mustYield, bool worthYielding)
            {
                MustYield = mustYield;
                WorthYielding = worthYielding;
            }
        }

        private struct BypassHoldCadenceSnapshot
        {
            public SceneKey SceneKey;
            public int WaypointIndex;
            public uint EvaluatedFrame;
            public uint ReevaluateAfterFrame;
            public bool ShouldHold;
            public bool CanClearAfterExit;
            public BypassConflictMode ConflictMode;
            public Entity Blocker;

            public BypassHoldCadenceSnapshot(
                SceneKey sceneKey,
                int waypointIndex,
                uint evaluatedFrame,
                uint reevaluateAfterFrame,
                bool shouldHold,
                bool canClearAfterExit,
                BypassConflictMode conflictMode,
                Entity blocker)
            {
                SceneKey = sceneKey;
                WaypointIndex = waypointIndex;
                EvaluatedFrame = evaluatedFrame;
                ReevaluateAfterFrame = reevaluateAfterFrame;
                ShouldHold = shouldHold;
                CanClearAfterExit = canClearAfterExit;
                ConflictMode = conflictMode;
                Blocker = blocker;
            }
        }

        private readonly struct BypassControlScope
        {
            public readonly Entity Vehicle;
            public readonly VehicleSceneBinding SceneBinding;
            public readonly SceneDefinition Scene;

            public BypassControlScope(
                Entity vehicle,
                VehicleSceneBinding sceneBinding,
                SceneDefinition scene)
            {
                Vehicle = vehicle;
                SceneBinding = sceneBinding;
                Scene = scene;
            }

            public Entity Line => Scene.Line;
            public int WaypointIndex => SceneBinding.WaypointIndex;
            public Entity CurrentBypassBuilding => Scene.CurrentBypassBuilding;
            public Entity NextBypassBuilding => Scene.NextBypassBuilding;
            public SceneKey SceneKey => Scene.Key;
        }

        private readonly struct BypassControlScopeCacheEntry
        {
            public readonly Entity Line;
            public readonly int WaypointIndex;
            public readonly BypassControlScope Scope;

            public BypassControlScopeCacheEntry(
                Entity line,
                int waypointIndex,
                BypassControlScope scope)
            {
                Line = line;
                WaypointIndex = waypointIndex;
                Scope = scope;
            }
        }

        private NativeHashMap<Entity, Entity> m_BypassYieldBlocker;
        private readonly Dictionary<Entity, BypassControlScopeCacheEntry> m_BypassControlScopeCache = new Dictionary<Entity, BypassControlScopeCacheEntry>();
        private readonly Dictionary<Entity, BypassHoldCadenceSnapshot> m_BypassHoldCadenceSnapshots = new Dictionary<Entity, BypassHoldCadenceSnapshot>();
        private readonly Dictionary<Entity, BypassConflictEpisode> m_BypassConflictEpisodes = new Dictionary<Entity, BypassConflictEpisode>();
        private bool m_BypassRuntimeEnabled = true;
    }
}
