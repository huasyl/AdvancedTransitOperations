using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackQuery
    {
        private readonly TrackState m_State;

        internal TrackQuery(TrackState state)
        {
            m_State = state;
        }

        internal bool TryChain(Entity line, out LineTrackChain chain)
        {
            if (!m_State.TryChain(line, out chain)
                || chain == null
                || !chain.ChainComplete
                || chain.TrackAtoms == null
                || chain.TrackAtoms.Count == 0
                || chain.SegmentRanges == null
                || chain.SegmentRanges.Count == 0)
            {
                chain = null;
                return false;
            }

            return true;
        }

        internal bool TryProfile(Entity line, out LineTraversalProfile profile)
        {
            profile = null;
            if (!m_State.TryChain(line, out LineTrackChain chain) || chain?.TraversalProfile == null)
                return false;

            profile = chain.TraversalProfile;
            return true;
        }
    }
}
