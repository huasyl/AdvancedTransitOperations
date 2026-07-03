using System.Collections.Generic;
using RapidTransitMod.TrackProjection;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct OrderedLineVehicleEntry
    {
        public readonly Entity Vehicle;
        public readonly LineRunningVehicleSnapshot RunningVehicle;
        public readonly float OwnLineAtomCoordinate;
        public readonly int TraversalPhaseIndex;
        public readonly int TraversalPhaseStartAtomIndex;
        public readonly int TraversalPhaseEndAtomExclusive;

        public OrderedLineVehicleEntry(
            Entity vehicle,
            LineRunningVehicleSnapshot runningVehicle,
            float ownLineAtomCoordinate,
            int traversalPhaseIndex,
            int traversalPhaseStartAtomIndex,
            int traversalPhaseEndAtomExclusive)
        {
            Vehicle = vehicle;
            RunningVehicle = runningVehicle;
            OwnLineAtomCoordinate = ownLineAtomCoordinate;
            TraversalPhaseIndex = traversalPhaseIndex;
            TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
            TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
        }
    }

    internal readonly struct OrderedLinePhaseRange
    {
        public readonly int TraversalPhaseIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int StartEntryIndex;
        public readonly int EndEntryIndexExclusive;

        public OrderedLinePhaseRange(
            int traversalPhaseIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int startEntryIndex,
            int endEntryIndexExclusive)
        {
            TraversalPhaseIndex = traversalPhaseIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            StartEntryIndex = startEntryIndex;
            EndEntryIndexExclusive = endEntryIndexExclusive;
        }
    }

    internal readonly struct OrderedSceneQueryWindow
    {
        public readonly int TraversalPhaseIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;

        public OrderedSceneQueryWindow(int traversalPhaseIndex, int startAtomIndex, int endAtomIndexExclusive)
        {
            TraversalPhaseIndex = traversalPhaseIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
        }
    }

    internal sealed class LineOrderedRuntimeState
    {
        public Entity Line;
        public ulong ChainSignature;
        public uint LastRefreshFrame;
        public uint LastFullSortFrame;
        public readonly List<OrderedLineVehicleEntry> Entries = new List<OrderedLineVehicleEntry>();
        public readonly List<OrderedLinePhaseRange> PhaseRanges = new List<OrderedLinePhaseRange>();
        public readonly Dictionary<Entity, OrderedLineVehicleEntry> ScratchEntriesByVehicle = new Dictionary<Entity, OrderedLineVehicleEntry>();
        public readonly List<OrderedSceneQueryWindow> ScratchQueryWindows = new List<OrderedSceneQueryWindow>();
    }
}
