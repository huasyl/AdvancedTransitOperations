using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal struct DwellObservation
    {
        public float AverageFrames;
        public int SampleCount;
    }

    internal struct StationDwellObservation
    {
        public float AverageFrames;
        public int SampleCount;
        public uint LastObservedFrame;
    }

    internal struct DwellSession
    {
        public Entity Line;
        public int WaypointIndex;
        public uint StartFrame;

        public DwellSession(Entity line, int waypointIndex, uint startFrame)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            StartFrame = startFrame;
        }
    }

    internal sealed class DwellStore
    {
        private NativeHashMap<Entity, uint> m_StartFrames;
        private readonly Dictionary<ulong, DwellObservation> m_Waypoints =
            new Dictionary<ulong, DwellObservation>();
        private readonly Dictionary<string, StationDwellObservation> m_Stations =
            new Dictionary<string, StationDwellObservation>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, DwellSession> m_Sessions =
            new Dictionary<Entity, DwellSession>();

        internal ref NativeHashMap<Entity, uint> StartFrames => ref m_StartFrames;
        internal Dictionary<ulong, DwellObservation> Waypoints => m_Waypoints;
        internal Dictionary<string, StationDwellObservation> Stations => m_Stations;
        internal Dictionary<Entity, DwellSession> Sessions => m_Sessions;

        internal void Init()
        {
            m_StartFrames = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
        }

        internal void Dispose()
        {
            if (m_StartFrames.IsCreated) m_StartFrames.Dispose();
        }

        internal void Clear()
        {
            m_StartFrames.Clear();
            m_Waypoints.Clear();
            m_Stations.Clear();
            m_Sessions.Clear();
        }

        internal void Remove(Entity vehicle)
        {
            m_StartFrames.Remove(vehicle);
            m_Sessions.Remove(vehicle);
        }

        internal void Begin(Entity vehicle, Entity line, int waypointIndex, uint frame) =>
            m_Sessions[vehicle] = new DwellSession(line, waypointIndex, frame);

        internal bool End(Entity vehicle, out DwellSession session)
        {
            if (!m_Sessions.TryGetValue(vehicle, out session)) return false;
            m_Sessions.Remove(vehicle);
            return true;
        }

        internal void SetStart(Entity vehicle, uint frame) =>
            m_StartFrames[vehicle] = frame;

        internal bool TryStart(Entity vehicle, out uint frame) =>
            m_StartFrames.TryGetValue(vehicle, out frame);

        internal bool RemoveStart(Entity vehicle) =>
            m_StartFrames.Remove(vehicle);

        internal void RecordWaypoint(ulong key, DwellObservation observation) =>
            m_Waypoints[key] = observation;

        internal void RecordStation(string key, StationDwellObservation observation) =>
            m_Stations[key] = observation;

        internal bool TryWaypoint(ulong key, out DwellObservation observation) =>
            m_Waypoints.TryGetValue(key, out observation);

        internal bool TryStation(string key, out StationDwellObservation observation) =>
            m_Stations.TryGetValue(key, out observation);
    }
}
