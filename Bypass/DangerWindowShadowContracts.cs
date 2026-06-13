using System.Collections.Generic;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    // FROZEN 2026-06-11: retained with the shadow implementation for the corresponding work card.
    // Runtime compare and selection-panel hooks are intentionally disconnected.
    internal readonly struct DangerWindowShadowStatus
    {
        public readonly bool InScope;
        public readonly bool Available;
        public readonly bool HasBlocker;
        public readonly Entity BlockerVehicle;
        public readonly string ReasonCode;

        public DangerWindowShadowStatus(
            bool inScope,
            bool available,
            bool hasBlocker,
            Entity blockerVehicle,
            string reasonCode)
        {
            InScope = inScope;
            Available = available;
            HasBlocker = hasBlocker;
            BlockerVehicle = blockerVehicle;
            ReasonCode = reasonCode ?? string.Empty;
        }

        public string ToAlertText()
        {
            if (HasBlocker && BlockerVehicle != Entity.Null)
                return "shadow-hold-for:" + BlockerVehicle.Index;
            if (Available)
                return "shadow-clear:" + ReasonCode;
            return "shadow-unavailable:" + ReasonCode;
        }
    }

    internal readonly struct DangerWindowShadowDecision
    {
        public readonly bool Available;
        public readonly bool HasBlocker;
        public readonly Entity BlockerVehicle;
        public readonly Entity ExpressLine;
        public readonly string MissReason;
        public readonly int LocalProtectedIntervalIndex;
        public readonly int MatchedRelationIndex;
        public readonly int MatchedTrunkIndex;
        public readonly int DangerWindowStartAtomIndex;
        public readonly int DangerWindowEndAtomIndexExclusive;
        public readonly float LocalClearFrames;
        public readonly float SafeEntryDeadlineFrames;

        public DangerWindowShadowDecision(
            bool available,
            bool hasBlocker,
            Entity blockerVehicle,
            Entity expressLine,
            string missReason,
            int localProtectedIntervalIndex,
            int matchedRelationIndex,
            int matchedTrunkIndex,
            int dangerWindowStartAtomIndex,
            int dangerWindowEndAtomIndexExclusive,
            float localClearFrames,
            float safeEntryDeadlineFrames)
        {
            Available = available;
            HasBlocker = hasBlocker;
            BlockerVehicle = blockerVehicle;
            ExpressLine = expressLine;
            MissReason = missReason ?? string.Empty;
            LocalProtectedIntervalIndex = localProtectedIntervalIndex;
            MatchedRelationIndex = matchedRelationIndex;
            MatchedTrunkIndex = matchedTrunkIndex;
            DangerWindowStartAtomIndex = dangerWindowStartAtomIndex;
            DangerWindowEndAtomIndexExclusive = dangerWindowEndAtomIndexExclusive;
            LocalClearFrames = localClearFrames;
            SafeEntryDeadlineFrames = safeEntryDeadlineFrames;
        }
    }

    internal readonly struct DangerWindowSegment
    {
        public readonly Entity ExpressLine;
        public readonly ulong ExpressChainSignature;
        public readonly int TraversalPhaseIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int ExpressCorridorStartAtomIndex;
        public readonly int ExpressCorridorEndAtomIndexExclusive;
        public readonly TrunkSkeleton TrunkSkeleton;
        public readonly GlobalSharedTrunkSegment GlobalSharedTrunkSegment;

        public DangerWindowSegment(
            Entity expressLine,
            ulong expressChainSignature,
            int traversalPhaseIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int expressCorridorStartAtomIndex,
            int expressCorridorEndAtomIndexExclusive,
            TrunkSkeleton trunkSkeleton,
            GlobalSharedTrunkSegment globalSharedTrunkSegment)
        {
            ExpressLine = expressLine;
            ExpressChainSignature = expressChainSignature;
            TraversalPhaseIndex = traversalPhaseIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            ExpressCorridorStartAtomIndex = expressCorridorStartAtomIndex;
            ExpressCorridorEndAtomIndexExclusive = expressCorridorEndAtomIndexExclusive;
            TrunkSkeleton = trunkSkeleton;
            GlobalSharedTrunkSegment = globalSharedTrunkSegment;
        }
    }

    internal readonly struct ShadowLineCursorEntry
    {
        public readonly Entity Vehicle;
        public readonly VehicleTrackCursor VehicleTrackCursor;
        public readonly float OwnLineAtomCoordinate;
        public readonly int TraversalPhaseIndex;
        public readonly int TraversalPhaseStartAtomIndex;
        public readonly int TraversalPhaseEndAtomExclusive;
        public readonly int NextTurnbackBoundaryAtomIndex;
        public readonly bool Boarding;

        public ShadowLineCursorEntry(
            Entity vehicle,
            VehicleTrackCursor vehicleTrackCursor,
            float ownLineAtomCoordinate,
            int traversalPhaseIndex,
            int traversalPhaseStartAtomIndex,
            int traversalPhaseEndAtomExclusive,
            int nextTurnbackBoundaryAtomIndex,
            bool boarding)
        {
            Vehicle = vehicle;
            VehicleTrackCursor = vehicleTrackCursor;
            OwnLineAtomCoordinate = ownLineAtomCoordinate;
            TraversalPhaseIndex = traversalPhaseIndex;
            TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
            TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
            NextTurnbackBoundaryAtomIndex = nextTurnbackBoundaryAtomIndex;
            Boarding = boarding;
        }
    }

    internal sealed class ShadowLineCursorFrame
    {
        public uint Frame;
        public Entity Line;
        public ulong ChainSignature;
        public readonly List<ShadowLineCursorEntry> Entries = new List<ShadowLineCursorEntry>();
    }
}
