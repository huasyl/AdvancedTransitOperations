using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.PassengerFlow.Jobs;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Sections
    {
        private readonly Dictionary<SectionCacheKey, Dictionary<int, SectionSegment[]>> m_Cache =
            new Dictionary<SectionCacheKey, Dictionary<int, SectionSegment[]>>();

        internal void Clear()
        {
            m_Cache.Clear();
        }

        internal SectionLoadEvent[] Expand(
            Port port,
            State state,
            PendingSample sample,
            DepartureLoadEvent loadEvent,
            uint frame)
        {
            if (port == null
                || state == null
                || sample.Line == Entity.Null
                || !port.HasWaypoints(sample.Line))
            {
                state?.Aggregates.RecordWarning(
                    sample.Mode,
                    Aggregates.WarningSectionTopologyMissing,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state != null ? state.CurrentBucket : new TimeBucketKey(0, 0),
                    frame);
                return Array.Empty<SectionLoadEvent>();
            }

            DynamicBuffer<RouteWaypoint> waypoints = port.Waypoints(sample.Line);
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
                return Array.Empty<SectionLoadEvent>();
            }

            SectionCacheKey cacheKey = new SectionCacheKey(sample.Line, chain.Signature);
            if (!m_Cache.TryGetValue(cacheKey, out Dictionary<int, SectionSegment[]> segmentsByWaypoint))
            {
                segmentsByWaypoint = BuildSegments(port, state, sample, waypoints, chain, frame);
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
                return Array.Empty<SectionLoadEvent>();
            }

            SectionLoadEvent[] events = new SectionLoadEvent[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                events[i] = new SectionLoadEvent(
                    sample.Mode,
                    sample.LineId,
                    segments[i].FromStationSakIndex,
                    segments[i].ToStationSakIndex,
                    loadEvent.PassengerCount);
            }

            return events;
        }

        private Dictionary<int, SectionSegment[]> BuildSegments(
            Port port,
            State state,
            PendingSample sample,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            uint frame)
        {
            Dictionary<int, SectionSegment[]> result = new Dictionary<int, SectionSegment[]>();
            List<TraversalEvent> stationEvents = new List<TraversalEvent>();
            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop
                    || traversalEvent.Kind == TraversalEventKind.Pass)
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
                    if (TryResolveEventStation(port, state, sample, currentEvent, frame, out StationKey currentStation))
                    {
                        if (!chainBroken)
                        {
                            segments.Add(new SectionSegment(
                                previousStation.Index,
                                currentStation.Index,
                                previousEventIndex: stationEvents[(cursor - 1 + stationEvents.Count) % stationEvents.Count].EventIndex,
                                toEventIndex: currentEvent.EventIndex,
                                includesPassStation: currentEvent.Kind == TraversalEventKind.Pass));
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
            PendingSample sample,
            TraversalEvent traversalEvent,
            uint frame,
            out StationKey station)
        {
            station = default;
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
                    if (state.Anchors.TryRegisterSak(sak, traversalEvent.Building, Entity.Null, traversalEvent.Building, out station))
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

        internal SectionCacheKey(Entity line, ulong signature)
        {
            m_Line = line;
            m_Signature = signature;
        }

        public bool Equals(SectionCacheKey other)
            => m_Line == other.m_Line && m_Signature == other.m_Signature;

        public override bool Equals(object obj)
            => obj is SectionCacheKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (m_Line.GetHashCode() * 397) ^ m_Signature.GetHashCode();
            }
        }
    }

    internal readonly struct SectionSegment
    {
        internal readonly int FromStationSakIndex;
        internal readonly int ToStationSakIndex;
        internal readonly int FromEventIndex;
        internal readonly int ToEventIndex;
        internal readonly bool IncludesPassStation;

        internal SectionSegment(
            int fromStationSakIndex,
            int toStationSakIndex,
            int previousEventIndex,
            int toEventIndex,
            bool includesPassStation)
        {
            FromStationSakIndex = fromStationSakIndex;
            ToStationSakIndex = toStationSakIndex;
            FromEventIndex = previousEventIndex;
            ToEventIndex = toEventIndex;
            IncludesPassStation = includesPassStation;
        }
    }

    internal readonly struct SectionLoadEvent
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly int FromStationSakIndex;
        internal readonly int ToStationSakIndex;
        internal readonly int PassengerCount;

        internal SectionLoadEvent(
            TransitMode mode,
            string lineId,
            int fromStationSakIndex,
            int toStationSakIndex,
            int passengerCount)
        {
            Mode = mode;
            LineId = lineId ?? string.Empty;
            FromStationSakIndex = fromStationSakIndex;
            ToStationSakIndex = toStationSakIndex;
            PassengerCount = passengerCount;
        }
    }
}
