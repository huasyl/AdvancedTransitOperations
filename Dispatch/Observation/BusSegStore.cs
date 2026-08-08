using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct BusSegKey : IEquatable<BusSegKey>
    {
        internal readonly Entity Line;
        internal readonly Entity FromWaypoint;
        internal readonly Entity FromStop;
        internal readonly Entity ToWaypoint;
        internal readonly Entity ToStop;

        internal BusSegKey(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop)
        {
            Line = line;
            FromWaypoint = fromWaypoint;
            FromStop = fromStop;
            ToWaypoint = toWaypoint;
            ToStop = toStop;
        }

        public bool Equals(BusSegKey other)
        {
            return Line == other.Line
                && FromWaypoint == other.FromWaypoint
                && FromStop == other.FromStop
                && ToWaypoint == other.ToWaypoint
                && ToStop == other.ToStop;
        }

        public override bool Equals(object obj) => obj is BusSegKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Line.GetHashCode();
                hash = (hash * 397) ^ FromWaypoint.GetHashCode();
                hash = (hash * 397) ^ FromStop.GetHashCode();
                hash = (hash * 397) ^ ToWaypoint.GetHashCode();
                return (hash * 397) ^ ToStop.GetHashCode();
            }
        }
    }

    internal readonly struct BusSegSession
    {
        internal readonly Entity Line;
        internal readonly Entity FromWaypoint;
        internal readonly Entity FromStop;
        internal readonly Entity ExpectedToWaypoint;
        internal readonly Entity ExpectedToStop;
        internal readonly uint StartFrame;

        internal BusSegSession(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity expectedToWaypoint,
            Entity expectedToStop,
            uint startFrame)
        {
            Line = line;
            FromWaypoint = fromWaypoint;
            FromStop = fromStop;
            ExpectedToWaypoint = expectedToWaypoint;
            ExpectedToStop = expectedToStop;
            StartFrame = startFrame;
        }
    }

    internal readonly struct BusSegObservation
    {
        internal readonly float EstimatedFrames;
        internal readonly int SampleCount;

        internal BusSegObservation(float estimatedFrames, int sampleCount)
        {
            EstimatedFrames = estimatedFrames;
            SampleCount = sampleCount;
        }
    }

    internal readonly struct BusSegSample
    {
        internal readonly BusSegKey Key;

        internal BusSegSample(BusSegKey key)
        {
            Key = key;
        }
    }

    internal sealed class BusSegStore
    {
        private readonly Dictionary<Entity, BusSegSession> m_Sessions =
            new Dictionary<Entity, BusSegSession>();
        private readonly Dictionary<BusSegKey, BusSegObservation> m_Observations =
            new Dictionary<BusSegKey, BusSegObservation>();

        internal IEnumerable<KeyValuePair<Entity, BusSegSession>> Sessions => m_Sessions;
        internal IEnumerable<KeyValuePair<BusSegKey, BusSegObservation>> Observations => m_Observations;

        internal void Begin(Entity vehicle, BusSegSession session)
        {
            if (vehicle != Entity.Null)
                m_Sessions[vehicle] = session;
        }

        internal bool TrySession(Entity vehicle, out BusSegSession session) =>
            m_Sessions.TryGetValue(vehicle, out session);

        internal void Cancel(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_Sessions.Remove(vehicle);
        }

        internal bool TryObservation(BusSegKey key, out BusSegObservation observation) =>
            m_Observations.TryGetValue(key, out observation);

        internal void Put(BusSegKey key, BusSegObservation observation)
        {
            m_Observations[key] = observation;
        }

        internal void Remove(BusSegKey key)
        {
            m_Observations.Remove(key);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            Cancel(vehicle);
        }

        internal void RemoveLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            var sessions = new List<Entity>();
            foreach (KeyValuePair<Entity, BusSegSession> pair in m_Sessions)
            {
                if (pair.Value.Line == line)
                    sessions.Add(pair.Key);
            }
            for (int i = 0; i < sessions.Count; i++)
                m_Sessions.Remove(sessions[i]);

            var observations = new List<BusSegKey>();
            foreach (KeyValuePair<BusSegKey, BusSegObservation> pair in m_Observations)
            {
                if (pair.Key.Line == line)
                    observations.Add(pair.Key);
            }
            for (int i = 0; i < observations.Count; i++)
                m_Observations.Remove(observations[i]);
        }

        internal void Clear()
        {
            m_Sessions.Clear();
            m_Observations.Clear();
        }
    }
}
