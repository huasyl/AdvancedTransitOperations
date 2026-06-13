using RapidTransitMod.TrackModel;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Bypass
{
    // FROZEN 2026-06-11: retained with the danger-window shadow prototype for the corresponding
    // work card. Runtime compare and panel hooks are intentionally disconnected.
    internal sealed class LineTraversalEtaIndex
    {
        public Entity Line { get; private set; }
        public ulong ChainSignature { get; private set; }
        public int RunSliceCount { get; private set; }
        public int EventCount { get; private set; }

        private readonly float[] m_PrefixRunFramesByAtom;
        private readonly float[] m_PrefixStopFramesByAtom;

        private LineTraversalEtaIndex(
            Entity line,
            ulong chainSignature,
            int runSliceCount,
            int eventCount,
            float[] prefixRunFramesByAtom,
            float[] prefixStopFramesByAtom)
        {
            Line = line;
            ChainSignature = chainSignature;
            RunSliceCount = runSliceCount;
            EventCount = eventCount;
            m_PrefixRunFramesByAtom = prefixRunFramesByAtom;
            m_PrefixStopFramesByAtom = prefixStopFramesByAtom;
        }

        internal bool Matches(LineTrackChain chain)
        {
            return chain != null
                && chain.LineEntity == Line
                && chain.Signature == ChainSignature
                && chain.TraversalProfile != null
                && chain.TraversalProfile.RunSlices.Count == RunSliceCount
                && chain.TraversalProfile.Events.Count == EventCount
                && m_PrefixRunFramesByAtom != null
                && m_PrefixStopFramesByAtom != null
                && m_PrefixRunFramesByAtom.Length == chain.TrackAtoms.Count + 1
                && m_PrefixStopFramesByAtom.Length == chain.TrackAtoms.Count + 1;
        }

        internal static bool TryBuild(
            IBypassAdmissionRuntimeContext runtime,
            LineTrackChain chain,
            out LineTraversalEtaIndex index)
        {
            index = null;
            if (runtime == null
                || chain == null
                || chain.TraversalProfile == null
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            int atomCount = chain.TrackAtoms.Count;
            float[] runFramesByAtom = new float[atomCount];
            float[] stopFramesByAtom = new float[atomCount];

            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                if (!runtime.TryGetEffectiveTraversalRunSliceFrames(chain.LineEntity, slice, out float effectiveRunFrames)
                    || !(effectiveRunFrames > 0f))
                {
                    return false;
                }

                int startAtomIndex = math.clamp(slice.StartAtomIndex, 0, atomCount - 1);
                int endAtomIndexExclusive = math.clamp(slice.EndAtomIndexExclusive, startAtomIndex + 1, atomCount);
                float sliceAtomLength = math.max(1f, endAtomIndexExclusive - startAtomIndex);
                float perAtomFrames = effectiveRunFrames / sliceAtomLength;
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                    runFramesByAtom[atomIndex] += perAtomFrames;
            }

            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.Kind != TraversalEventKind.Stop
                    || !(traversalEvent.StopFrames > 0f))
                {
                    continue;
                }

                int eventAtomIndex = math.clamp(traversalEvent.StartAtomIndex, 0, atomCount - 1);
                stopFramesByAtom[eventAtomIndex] += traversalEvent.StopFrames;
            }

            float[] prefixRunFramesByAtom = new float[atomCount + 1];
            float[] prefixStopFramesByAtom = new float[atomCount + 1];
            for (int atomIndex = 0; atomIndex < atomCount; atomIndex++)
            {
                prefixRunFramesByAtom[atomIndex + 1] = prefixRunFramesByAtom[atomIndex] + runFramesByAtom[atomIndex];
                prefixStopFramesByAtom[atomIndex + 1] = prefixStopFramesByAtom[atomIndex] + stopFramesByAtom[atomIndex];
            }

            index = new LineTraversalEtaIndex(
                chain.LineEntity,
                chain.Signature,
                chain.TraversalProfile.RunSlices.Count,
                chain.TraversalProfile.Events.Count,
                prefixRunFramesByAtom,
                prefixStopFramesByAtom);
            return true;
        }

        internal float FramesBetween(float fromCoordinate, int targetAtomIndexExclusive)
        {
            if (m_PrefixRunFramesByAtom == null
                || m_PrefixStopFramesByAtom == null
                || m_PrefixRunFramesByAtom.Length <= 1
                || targetAtomIndexExclusive < 0)
            {
                return float.MaxValue;
            }

            int atomCount = m_PrefixRunFramesByAtom.Length - 1;
            if (atomCount <= 0)
                return float.MaxValue;

            float clampedFromCoordinate = math.clamp(fromCoordinate, 0f, atomCount);
            int fromAtomIndex = math.clamp((int)math.floor(clampedFromCoordinate), 0, atomCount - 1);
            int toAtomIndexExclusive = math.clamp(targetAtomIndexExclusive, 0, atomCount);
            if (toAtomIndexExclusive <= fromAtomIndex)
                return 0f;

            float fromFraction = math.saturate(clampedFromCoordinate - fromAtomIndex);
            float frames = 0f;
            frames += m_PrefixRunFramesByAtom[toAtomIndexExclusive] - m_PrefixRunFramesByAtom[fromAtomIndex + 1];
            frames += m_PrefixStopFramesByAtom[toAtomIndexExclusive] - m_PrefixStopFramesByAtom[fromAtomIndex + 1];
            frames += (1f - fromFraction) * (m_PrefixRunFramesByAtom[fromAtomIndex + 1] - m_PrefixRunFramesByAtom[fromAtomIndex]);
            return math.max(0f, frames);
        }

        internal bool TryFindEarliestAtomReachingTargetWithinFrames(
            int phaseStartAtomIndex,
            int targetAtomIndexExclusive,
            float maxFrames,
            out int atomIndex)
        {
            atomIndex = -1;
            if (m_PrefixRunFramesByAtom == null
                || m_PrefixRunFramesByAtom.Length <= 1
                || !(maxFrames >= 0f))
            {
                return false;
            }

            int atomCount = m_PrefixRunFramesByAtom.Length - 1;
            int clampedTargetAtomIndexExclusive = math.clamp(targetAtomIndexExclusive, 0, atomCount);
            int left = math.clamp(phaseStartAtomIndex, 0, clampedTargetAtomIndexExclusive);
            int right = clampedTargetAtomIndexExclusive;
            int best = clampedTargetAtomIndexExclusive;
            while (left <= right)
            {
                int mid = left + ((right - left) / 2);
                float frames = FramesBetween(mid, clampedTargetAtomIndexExclusive);
                if (frames <= maxFrames)
                {
                    best = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }

            atomIndex = math.clamp(best, 0, atomCount);
            return true;
        }
    }
}
