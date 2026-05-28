using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal struct StopDwellObservation
    {
        public float AverageFrames;
        public int SampleCount;
    }

    internal struct StationStopDwellObservation
    {
        public float AverageFrames;
        public int SampleCount;
        public uint LastObservedFrame;
    }

    internal struct StopDwellSession
    {
        public Entity Line;
        public int WaypointIndex;
        public uint StartFrame;

        public StopDwellSession(Entity line, int waypointIndex, uint startFrame)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            StartFrame = startFrame;
        }
    }

    internal sealed class StopDwellStore
    {
        private NativeHashMap<Entity, uint> m_StopDwellStartFrame;
        private readonly Dictionary<ulong, StopDwellObservation> m_WaypointStopDwellObservations =
            new Dictionary<ulong, StopDwellObservation>();
        private readonly Dictionary<string, StationStopDwellObservation> m_StationStopDwellObservations =
            new Dictionary<string, StationStopDwellObservation>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, StopDwellSession> m_StopDwellSessions =
            new Dictionary<Entity, StopDwellSession>();

        internal ref NativeHashMap<Entity, uint> StartFrames => ref m_StopDwellStartFrame;
        internal Dictionary<ulong, StopDwellObservation> Waypoints => m_WaypointStopDwellObservations;
        internal Dictionary<string, StationStopDwellObservation> Stations => m_StationStopDwellObservations;
        internal Dictionary<Entity, StopDwellSession> Sessions => m_StopDwellSessions;

        internal void Init()
        {
            m_StopDwellStartFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
        }

        internal void Dispose()
        {
            if (m_StopDwellStartFrame.IsCreated) m_StopDwellStartFrame.Dispose();
        }

        internal void Clear()
        {
            m_StopDwellStartFrame.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StationStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
        }

        internal void Remove(Entity vehicle)
        {
            m_StopDwellStartFrame.Remove(vehicle);
            m_StopDwellSessions.Remove(vehicle);
        }

        internal void Begin(Entity vehicle, Entity line, int waypointIndex, uint frame) =>
            m_StopDwellSessions[vehicle] = new StopDwellSession(line, waypointIndex, frame);

        internal bool End(Entity vehicle, out StopDwellSession session)
        {
            if (!m_StopDwellSessions.TryGetValue(vehicle, out session)) return false;
            m_StopDwellSessions.Remove(vehicle);
            return true;
        }

        internal void SetStart(Entity vehicle, uint frame) =>
            m_StopDwellStartFrame[vehicle] = frame;

        internal bool TryStart(Entity vehicle, out uint frame) =>
            m_StopDwellStartFrame.TryGetValue(vehicle, out frame);

        internal bool RemoveStart(Entity vehicle) =>
            m_StopDwellStartFrame.Remove(vehicle);

        internal void RecordWaypoint(ulong key, StopDwellObservation observation) =>
            m_WaypointStopDwellObservations[key] = observation;

        internal void RecordStation(string key, StationStopDwellObservation observation) =>
            m_StationStopDwellObservations[key] = observation;

        internal bool TryWaypoint(ulong key, out StopDwellObservation observation) =>
            m_WaypointStopDwellObservations.TryGetValue(key, out observation);

        internal bool TryStation(string key, out StationStopDwellObservation observation) =>
            m_StationStopDwellObservations.TryGetValue(key, out observation);
    }
}
