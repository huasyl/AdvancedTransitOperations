using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal static class TraversalProfileQuery
    {
        internal static bool TryGetRunSliceForAtom(
            LineTrackChain chain,
            int atomIndex,
            out int sliceIndex,
            out TraversalRunSlice slice)
        {
            sliceIndex = -1;
            slice = default;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices == null
                || chain.TraversalProfile.AtomToRunSliceIndex == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            int clampedAtomIndex = math.clamp(atomIndex, 0, chain.TrackAtoms.Count - 1);
            if (clampedAtomIndex < 0 || clampedAtomIndex >= chain.TraversalProfile.AtomToRunSliceIndex.Length)
                return false;

            sliceIndex = chain.TraversalProfile.AtomToRunSliceIndex[clampedAtomIndex];
            if (sliceIndex < 0 || sliceIndex >= chain.TraversalProfile.RunSlices.Count)
                return false;

            slice = chain.TraversalProfile.RunSlices[sliceIndex];
            return true;
        }

        internal static bool TryGetNextPhysicalStationEvent(
            LineTrackChain chain,
            int atomIndex,
            out TraversalEvent stationEvent)
        {
            stationEvent = default;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || chain.TraversalProfile.Events.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            if (atomIndex >= chain.TrackAtoms.Count)
                return TryFindFirstStationEventAtOrAfterAtom(chain, 0, out stationEvent);

            int clampedAtomIndex = math.clamp(atomIndex, 0, chain.TrackAtoms.Count - 1);
            if (TryFindStationEventContainingAtom(chain, clampedAtomIndex, out stationEvent))
                return true;

            int searchStartAtomIndex = clampedAtomIndex;
            if (TryGetRunSliceForAtom(chain, clampedAtomIndex, out _, out TraversalRunSlice slice))
                searchStartAtomIndex = math.max(clampedAtomIndex, slice.EndAtomIndexExclusive);

            return TryFindFirstStationEventAtOrAfterAtom(chain, searchStartAtomIndex, out stationEvent);
        }

        private static bool TryFindStationEventContainingAtom(
            LineTrackChain chain,
            int atomIndex,
            out TraversalEvent stationEvent)
        {
            stationEvent = default;
            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[i];
                if (!IsStationEvent(candidate))
                    continue;

                int start = math.clamp(candidate.StartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                int endExclusive = math.clamp(math.max(start + 1, candidate.EndAtomIndexExclusive), start + 1, chain.TrackAtoms.Count);
                if (atomIndex >= start && atomIndex < endExclusive)
                {
                    stationEvent = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindFirstStationEventAtOrAfterAtom(
            LineTrackChain chain,
            int atomIndex,
            out TraversalEvent stationEvent)
        {
            stationEvent = default;
            int normalizedAtomIndex = atomIndex >= chain.TrackAtoms.Count
                ? 0
                : math.max(0, atomIndex);
            bool found = false;
            int bestStartAtomIndex = int.MaxValue;

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[i];
                if (!IsStationEvent(candidate))
                    continue;

                int start = math.clamp(candidate.StartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                if (start < normalizedAtomIndex || start >= bestStartAtomIndex)
                    continue;

                stationEvent = candidate;
                bestStartAtomIndex = start;
                found = true;
            }

            if (found)
                return true;

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[i];
                if (!IsStationEvent(candidate))
                    continue;

                int start = math.clamp(candidate.StartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                if (start >= bestStartAtomIndex)
                    continue;

                stationEvent = candidate;
                bestStartAtomIndex = start;
                found = true;
            }

            return found;
        }

        private static bool IsStationEvent(TraversalEvent traversalEvent)
        {
            return traversalEvent.Kind == TraversalEventKind.Stop
                || traversalEvent.Kind == TraversalEventKind.Pass;
        }
    }
}
