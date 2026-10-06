using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal enum SectionKind { Track, Stops }

    internal sealed class Sections
    {
        private readonly Dictionary<SectionCacheKey, Dictionary<int, SectionSegment[]>> m_Cache =
            new Dictionary<SectionCacheKey, Dictionary<int, SectionSegment[]>>();

        internal void Clear()
        {
            m_Cache.Clear();
        }

        internal void InvalidateLine(Entity line)
        {
            List<SectionCacheKey> remove = new List<SectionCacheKey>();
            foreach (SectionCacheKey key in m_Cache.Keys)
                if (key.IsLine(line)) remove.Add(key);
            foreach (SectionCacheKey key in remove) m_Cache.Remove(key);
        }

        internal static bool Supports(TransitMode mode)
            => mode == TransitMode.Train || mode == TransitMode.Subway || mode == TransitMode.Tram || mode == TransitMode.Bus;

        internal SectionSegment[] PrepareTrack(
            Port port,
            State state,
            OpenStop sample,
            uint frame)
        {
            if (!Supports(sample.Mode))
                return Array.Empty<SectionSegment>();

            if (sample.Mode == TransitMode.Bus)
                return Array.Empty<SectionSegment>();

            if (!port.HasWaypoints(sample.Line))
            {
                state.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionTopologyMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                return Array.Empty<SectionSegment>();
            }

            DynamicBuffer<RouteWaypoint> waypoints = port.Waypoints(sample.Line);
            if (sample.OpenWaypointIndex < 0 || sample.OpenWaypointIndex >= waypoints.Length
                || waypoints[sample.OpenWaypointIndex].m_Waypoint != sample.Position.Waypoint
                || !state.Anchors.TryForWaypoint(port, sample.Line, sample.OpenWaypointIndex, out StationKey origin)
                || origin.Index != sample.OpenStationSakIndex)
                return Array.Empty<SectionSegment>();
            if (!port.TryTrackChain(sample.Line, waypoints, out LineTrackChain chain)
                || chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || chain.TraversalProfile.Events.Count == 0)
            {
                state.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionTopologyMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                return Array.Empty<SectionSegment>();
            }

            SectionCacheKey cacheKey = new SectionCacheKey(
                sample.Line,
                chain.Signature,
                chain.TraversalSignature);
            if (!m_Cache.TryGetValue(cacheKey, out Dictionary<int, SectionSegment[]> segmentsByWaypoint))
            {
                segmentsByWaypoint = BuildSegments(port, state, sample, chain, frame);
                m_Cache[cacheKey] = segmentsByWaypoint;
            }

            if (!segmentsByWaypoint.TryGetValue(sample.OpenWaypointIndex, out SectionSegment[] segments)
                || segments == null
                || segments.Length == 0)
            {
                state.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionTopologyMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                return Array.Empty<SectionSegment>();
            }

            return segments;
        }

        internal static SectionSegment[] PrepareStops(Port port, State state, OpenStop stop)
        {
            if (!port.TryRoutePlan(stop.Line, out Dispatch.Lines.RoutePlan plan))
                return Array.Empty<SectionSegment>();
            for (int i = 0; i < plan.Stops.Length; i++)
            {
                Dispatch.Lines.RouteStopRef from = plan.Stops[i];
                if (from.Waypoint != stop.Position.Waypoint
                    || from.StationOccurrence != stop.Position.StationOccurrence
                    || !state.Anchors.TryGetIndex(from.StopKey, out int fromStation)
                    || fromStation != stop.OpenStationSakIndex)
                    continue;
                Dispatch.Lines.RouteStopRef to = plan.Stops[(i + 1) % plan.Stops.Length];
                if (!state.Anchors.TryForWaypoint(port, stop.Line, to.WaypointIndex, out StationKey destination))
                    return Array.Empty<SectionSegment>();
                return new[] { new SectionSegment(fromStation, destination.Index,
                    from.StationOccurrence, to.StationOccurrence) };
            }
            return Array.Empty<SectionSegment>();
        }

        private Dictionary<int, SectionSegment[]> BuildSegments(
            Port port,
            State state,
            OpenStop sample,
            LineTrackChain chain,
            uint frame)
        {
            Dictionary<int, SectionSegment[]> result = new Dictionary<int, SectionSegment[]>();
            List<TraversalEvent> stationEvents = new List<TraversalEvent>();
            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop
                    || traversalEvent.Kind == TraversalEventKind.Pass
                    || traversalEvent.Kind == TraversalEventKind.BreakBoundary)
                {
                    stationEvents.Add(traversalEvent);
                }
            }

            if (stationEvents.Count < 2)
                return result;

            for (int startIndex = 0; startIndex < stationEvents.Count; startIndex++)
            {
                TraversalEvent startEvent = stationEvents[startIndex];
                if (startEvent.Kind != TraversalEventKind.Stop || startEvent.WaypointIndex < 0)
                    continue;

                if (!TryResolveEventStation(port, state, sample, startEvent, frame, out StationKey previousStation))
                    continue;

                List<SectionSegment> segments = new List<SectionSegment>();
                bool chainBroken = false;
                int cursor = (startIndex + 1) % stationEvents.Count;
                int guard = 0;
                while (guard++ < stationEvents.Count)
                {
                    TraversalEvent currentEvent = stationEvents[cursor];
                    if (currentEvent.Kind == TraversalEventKind.BreakBoundary)
                    {
                        chainBroken = true;
                        cursor = (cursor + 1) % stationEvents.Count;
                        continue;
                    }
                    if (TryResolveEventStation(port, state, sample, currentEvent, frame, out StationKey currentStation))
                    {
                        if (!chainBroken)
                        {
                            segments.Add(new SectionSegment(
                                previousStation.Index,
                                currentStation.Index));
                        }

                        previousStation = currentStation;
                        chainBroken = false;
                    }
                    else
                    {
                        chainBroken = true;
                    }

                    if (currentEvent.Kind == TraversalEventKind.Stop)
                        break;

                    cursor = (cursor + 1) % stationEvents.Count;
                }

                if (segments.Count > 0)
                    result[startEvent.WaypointIndex] = segments.ToArray();
            }

            return result;
        }

        private static bool TryResolveEventStation(
            Port port,
            State state,
            OpenStop sample,
            TraversalEvent traversalEvent,
            uint frame,
            out StationKey station)
        {
            station = default;
            if (sample.Mode == TransitMode.Tram)
            {
                if (string.IsNullOrEmpty(traversalEvent.StationId))
                    return false;

                Entity entity = traversalEvent.Building;
                bool isStop = entity != Entity.Null && port.IsTransportStop(entity);
                return state.Anchors.TryRegisterSak(
                    traversalEvent.StationId,
                    entity,
                    isStop ? entity : Entity.Null,
                    isStop ? Entity.Null : entity,
                    out station);
            }
            if (traversalEvent.Kind == TraversalEventKind.Stop)
            {
                if (traversalEvent.WaypointIndex >= 0
                    && state.Anchors.TryForWaypoint(port, sample.Line, traversalEvent.WaypointIndex, out station))
                {
                    return true;
                }

                state.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionAnchorMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                return false;
            }

            if (traversalEvent.Kind == TraversalEventKind.Pass)
            {
                if (traversalEvent.Building != Entity.Null)
                {
                    string sak = port.EnsureSak(traversalEvent.Building);
                    if (port.IsTransportStop(traversalEvent.Building))
                    {
                        if (state.Anchors.TryRegisterSak(
                                sak,
                                traversalEvent.Building,
                                traversalEvent.Building,
                                Entity.Null,
                                out station))
                        {
                            return true;
                        }
                    }

                    else if (state.Anchors.TryRegisterSak(sak, traversalEvent.Building, Entity.Null, traversalEvent.Building, out station))
                        return true;
                }

                state.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionPassAnchorMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
            }

            return false;
        }
    }

    internal readonly struct SectionCacheKey : IEquatable<SectionCacheKey>
    {
        private readonly Entity m_Line;
        private readonly ulong m_Signature;
        private readonly ulong m_TraversalSignature;

        internal SectionCacheKey(Entity line, ulong signature, ulong traversalSignature)
        {
            m_Line = line;
            m_Signature = signature;
            m_TraversalSignature = traversalSignature;
        }

        internal bool IsLine(Entity line) => m_Line == line;

        public bool Equals(SectionCacheKey other)
            => m_Line == other.m_Line
                && m_Signature == other.m_Signature
                && m_TraversalSignature == other.m_TraversalSignature;

        public override bool Equals(object obj)
            => obj is SectionCacheKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((m_Line.GetHashCode() * 397) ^ m_Signature.GetHashCode())
                    * 397 ^ m_TraversalSignature.GetHashCode();
            }
        }
    }

    internal readonly struct SectionSegment
    {
        internal readonly int FromStationSakIndex;
        internal readonly int ToStationSakIndex;
        internal readonly int FromStationOccurrence;
        internal readonly int ToStationOccurrence;

        internal SectionSegment(
            int fromStationSakIndex,
            int toStationSakIndex,
            int fromStationOccurrence = 0,
            int toStationOccurrence = 0)
        {
            FromStationSakIndex = fromStationSakIndex;
            ToStationSakIndex = toStationSakIndex;
            FromStationOccurrence = fromStationOccurrence;
            ToStationOccurrence = toStationOccurrence;
        }
    }

}
