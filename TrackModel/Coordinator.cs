using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal delegate bool TrackChainBuild(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain);
    internal delegate void TrackSharedRefresh();

    internal sealed class TrackModelCoordinator
    {
        private readonly TrackModelStore m_Store;
        private readonly TrackModelBuilder m_Builder;

        public TrackModelCoordinator(TrackModelStore store, TrackModelBuilder builder)
        {
            m_Store = store;
            m_Builder = builder;
        }

        public bool Ensure(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain, TrackChainBuild build)
        {
            chain = null;
            if (line == Entity.Null || build == null)
                return false;

            return build(line, waypoints, out chain);
        }

        public bool Invalidate(Entity line, out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null)
                return false;

            m_Store.MarkDirty(line);
            m_Builder.MarkDirty();
            return m_Store.Remove(line, out chain);
        }

        public void InvalidateAll()
        {
            m_Store.Clear();
            m_Builder.Clear();
        }

        public void RefreshShared(TrackSharedRefresh refresh)
        {
            if (refresh == null || !m_Builder.Dirty())
                return;

            refresh();
        }
    }
}
