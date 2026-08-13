using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal readonly struct TramPassRange
    {
        internal readonly int AtomIndex;
        internal readonly Entity Stop;
        internal readonly string StationId;

        internal TramPassRange(int atomIndex, Entity stop, string stationId)
        {
            AtomIndex = atomIndex;
            Stop = stop;
            StationId = stationId ?? string.Empty;
        }
    }

    internal sealed class TramStopIndex
    {
        private const int AuditItemsPerTick = 4;
        private const uint AuditIntervalFrames = 360u;
        private readonly TrackSupport m_Support;
        private readonly Dictionary<EntryKey, Entry> m_Entries = new Dictionary<EntryKey, Entry>();
        private readonly Dictionary<Entity, List<Entry>> m_ByLane = new Dictionary<Entity, List<Entry>>();
        private readonly Dictionary<Entity, List<Entry>> m_ByLine = new Dictionary<Entity, List<Entry>>();
        private readonly Dictionary<string, List<Entry>> m_ByName =
            new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly Dictionary<SpatialKey, List<Entry>> m_ByCell =
            new Dictionary<SpatialKey, List<Entry>>();
        private readonly Dictionary<Entity, HashSet<Entity>> m_LinesByLane = new Dictionary<Entity, HashSet<Entity>>();
        private readonly Dictionary<Entity, List<Entity>> m_LanesByLine = new Dictionary<Entity, List<Entity>>();
        private readonly List<EntryKey> m_AuditOrder = new List<EntryKey>();
        private readonly Queue<Entity> m_DirtyLines = new Queue<Entity>();
        private readonly HashSet<Entity> m_DirtySet = new HashSet<Entity>();
        private int m_AuditCursor;
        private bool m_AuditSoon;
        private uint m_NextAuditFrame;

        internal TramStopIndex(TrackSupport support)
        {
            m_Support = support ?? throw new ArgumentNullException(nameof(support));
        }

        internal void RequestAudit() => m_AuditSoon = true;

        internal void Clear()
        {
            m_Entries.Clear();
            m_ByLane.Clear();
            m_ByLine.Clear();
            m_ByName.Clear();
            m_ByCell.Clear();
            m_LinesByLane.Clear();
            m_LanesByLine.Clear();
            m_AuditOrder.Clear();
            m_DirtyLines.Clear();
            m_DirtySet.Clear();
            m_AuditCursor = 0;
            m_AuditSoon = false;
            m_NextAuditFrame = 0u;
        }

        internal void RegisterLine(
            Entity line,
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (line == Entity.Null || waypoints.Length == 0)
                return;

            RemoveLine(line);
            RegisterChainLanes(line, chain);
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!TryReadEntry(line, waypoints[waypointIndex].m_Waypoint, waypointIndex, out Entry entry))
                    continue;
                AddEntry(entry);
            }
        }

        internal void RemoveLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            if (m_ByLine.TryGetValue(line, out List<Entry> entries))
            {
                Entry[] removed = entries.ToArray();
                for (int i = 0; i < removed.Length; i++)
                    RemoveEntry(removed[i]);
            }
            if (m_LanesByLine.TryGetValue(line, out List<Entity> chainLanes))
            {
                for (int i = 0; i < chainLanes.Count; i++)
                {
                    if (!m_LinesByLane.TryGetValue(chainLanes[i], out HashSet<Entity> lines))
                        continue;
                    lines.Remove(line);
                    if (lines.Count == 0)
                        m_LinesByLane.Remove(chainLanes[i]);
                }
                m_LanesByLine.Remove(line);
            }
        }

        internal void Tick()
        {
            if (m_AuditOrder.Count == 0)
                return;

            uint nowFrame = m_Support.FrameIndex;
            if (!m_AuditSoon && nowFrame < m_NextAuditFrame)
                return;

            int budget = AuditItemsPerTick;
            m_AuditSoon = false;
            m_NextAuditFrame = unchecked(nowFrame + AuditIntervalFrames);
            for (int i = 0; i < budget && m_AuditOrder.Count > 0; i++)
            {
                if (m_AuditCursor >= m_AuditOrder.Count)
                    m_AuditCursor = 0;
                EntryKey key = m_AuditOrder[m_AuditCursor++];
                if (!m_Entries.TryGetValue(key, out Entry oldEntry))
                    continue;
                if (!m_Support.EntityManager.HasBuffer<RouteWaypoint>(oldEntry.Line))
                {
                    RemoveLine(oldEntry.Line);
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_Support.EntityManager.GetBuffer<RouteWaypoint>(oldEntry.Line, true);
                Entry current = null;
                bool changed = oldEntry.WaypointIndex < 0 || oldEntry.WaypointIndex >= waypoints.Length;
                if (!changed)
                {
                    changed = !TryReadEntry(
                        oldEntry.Line,
                        waypoints[oldEntry.WaypointIndex].m_Waypoint,
                        oldEntry.WaypointIndex,
                        out current)
                        || !SameFingerprint(oldEntry, current);
                }
                if (changed)
                {
                    RemoveEntry(oldEntry);
                    if (current != null)
                        AddEntry(current);
                }
            }
        }

        internal void DrainDirtyLines(int budget, List<Entity> output)
        {
            if (output == null || budget <= 0)
                return;
            while (budget-- > 0 && m_DirtyLines.Count > 0)
            {
                Entity line = m_DirtyLines.Dequeue();
                m_DirtySet.Remove(line);
                output.Add(line);
            }
        }

        internal bool TryGetStationId(Entity line, int waypointIndex, out string stationId)
        {
            stationId = string.Empty;
            if (!m_ByLine.TryGetValue(line, out List<Entry> entries))
                return false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].WaypointIndex != waypointIndex)
                    continue;
                stationId = entries[i].GroupId;
                return !string.IsNullOrEmpty(stationId);
            }
            return false;
        }

        internal void CollectPasses(Entity line, LineTrackChain chain, List<TramPassRange> output)
        {
            if (chain == null || output == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> pair in chain.AtomIndicesByLane)
            {
                if (!m_ByLane.TryGetValue(pair.Key, out List<Entry> entries))
                    continue;
                List<int> atomIndices = pair.Value;
                for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    Entry entry = entries[entryIndex];
                    if (entry.Line == line || string.IsNullOrEmpty(entry.GroupId))
                        continue;
                    for (int atomListIndex = 0; atomListIndex < atomIndices.Count; atomListIndex++)
                    {
                        int atomIndex = atomIndices[atomListIndex];
                        if (atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                            continue;
                        TrackAtom atom = chain.TrackAtoms[atomIndex];
                        float low = math.min(atom.TargetDelta.x, atom.TargetDelta.y);
                        float high = math.max(atom.TargetDelta.x, atom.TargetDelta.y);
                        if (entry.CurvePosition < low - 0.001f || entry.CurvePosition > high + 0.001f)
                            continue;
                        output.Add(new TramPassRange(atomIndex, entry.Stop, entry.GroupId));
                    }
                }
            }
        }

        private bool TryReadEntry(Entity line, Entity waypoint, int waypointIndex, out Entry entry)
        {
            entry = null;
            EntityManager entities = m_Support.EntityManager;
            Entity stop = m_Support.Stop(waypoint);
            if (waypoint == Entity.Null || stop == Entity.Null
                || !entities.Exists(stop)
                || !entities.HasComponent<TransportStop>(stop)
                || !entities.HasComponent<RouteLane>(waypoint))
            {
                return false;
            }

            RouteLane routeLane = entities.GetComponentData<RouteLane>(waypoint);
            Entity lane = routeLane.m_StartLane != Entity.Null ? routeLane.m_StartLane : routeLane.m_EndLane;
            float curvePosition = routeLane.m_StartLane != Entity.Null ? routeLane.m_StartCurvePos : routeLane.m_EndCurvePos;
            if (lane == Entity.Null || !entities.Exists(lane) || !TryPosition(waypoint, stop, out Entity positionEntity, out float3 position))
                return false;

            string name = m_Support.StopName(stop);
            string key = m_Support.StopKey(stop);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key))
                return false;

            entry = new Entry
            {
                Line = line,
                Key = new EntryKey(line, waypointIndex),
                Stop = stop,
                Waypoint = waypoint,
                WaypointIndex = waypointIndex,
                Lane = lane,
                CurvePosition = curvePosition,
                Name = name,
                StopKey = key,
                PositionEntity = positionEntity,
                Position = position
            };
            return true;
        }

        private bool TryPosition(Entity waypoint, Entity stop, out Entity source, out float3 position)
        {
            EntityManager entities = m_Support.EntityManager;
            source = Entity.Null;
            position = default;
            Entity connected = waypoint != Entity.Null && entities.HasComponent<Connected>(waypoint)
                ? entities.GetComponentData<Connected>(waypoint).m_Connected
                : Entity.Null;
            if (TryTransform(connected, out position))
            {
                source = connected;
                return true;
            }
            if (TryTransform(waypoint, out position) || TryRoutePosition(waypoint, out position))
            {
                source = waypoint;
                return true;
            }
            if (TryTransform(stop, out position) || TryRoutePosition(stop, out position))
            {
                source = stop;
                return true;
            }
            return false;
        }

        private bool TryTransform(Entity entity, out float3 position)
        {
            position = default;
            EntityManager entities = m_Support.EntityManager;
            if (entity == Entity.Null || !entities.Exists(entity) || !entities.HasComponent<Transform>(entity))
                return false;
            position = entities.GetComponentData<Transform>(entity).m_Position;
            return true;
        }

        private bool TryRoutePosition(Entity entity, out float3 position)
        {
            position = default;
            EntityManager entities = m_Support.EntityManager;
            if (entity == Entity.Null || !entities.Exists(entity) || !entities.HasComponent<Game.Routes.Position>(entity))
                return false;
            position = entities.GetComponentData<Game.Routes.Position>(entity).m_Position;
            return true;
        }

        private void AddEntry(Entry entry)
        {
            if (entry == null || entry.Line == Entity.Null)
                return;

            m_Entries[entry.Key] = entry;
            m_AuditOrder.Add(entry.Key);
            AddIndexed(m_ByLine, entry.Line, entry);
            AddIndexed(m_ByLane, entry.Lane, entry);
            AddIndexed(m_ByName, entry.Name, entry);
            AddIndexed(m_ByCell, Cell(entry.Position), entry);
            RebuildComponent(CollectComponent(entry));
            MarkLinesForLane(entry.Lane);
        }

        private void RemoveEntry(Entry entry)
        {
            if (entry == null || !m_Entries.TryGetValue(entry.Key, out Entry current))
                return;

            List<Entry> component = CollectComponent(current);
            m_Entries.Remove(current.Key);
            m_AuditOrder.Remove(current.Key);
            RemoveIndexed(m_ByLine, current.Line, current);
            RemoveIndexed(m_ByLane, current.Lane, current);
            RemoveIndexed(m_ByName, current.Name, current);
            RemoveIndexed(m_ByCell, Cell(current.Position), current);
            RebuildComponent(component);
            MarkLinesForLane(current.Lane);
        }

        private List<Entry> CollectComponent(Entry seed)
        {
            var result = new List<Entry>();
            if (seed == null || !m_Entries.TryGetValue(seed.Key, out Entry current))
                return result;

            var pending = new Stack<Entry>();
            var visited = new HashSet<EntryKey>();
            pending.Push(current);
            while (pending.Count > 0)
            {
                Entry entry = pending.Pop();
                if (entry == null || !visited.Add(entry.Key)
                    || !m_Entries.TryGetValue(entry.Key, out Entry indexed)
                    || !ReferenceEquals(entry, indexed))
                {
                    continue;
                }

                result.Add(entry);
                List<Entry> neighbors = NearbyEntries(entry);
                for (int i = 0; i < neighbors.Count; i++)
                    pending.Push(neighbors[i]);
            }
            return result;
        }

        private List<Entry> NearbyEntries(Entry entry)
        {
            var result = new List<Entry>();
            if (entry == null)
                return result;

            SpatialKey cell = Cell(entry.Position);
            for (int x = cell.X - 1; x <= cell.X + 1; x++)
            for (int y = cell.Y - 1; y <= cell.Y + 1; y++)
            for (int z = cell.Z - 1; z <= cell.Z + 1; z++)
            {
                if (!m_ByCell.TryGetValue(new SpatialKey(x, y, z), out List<Entry> candidates))
                    continue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Entry candidate = candidates[i];
                    if (candidate.Key.Equals(entry.Key)
                        || !string.Equals(candidate.Name, entry.Name, StringComparison.Ordinal)
                        || math.distancesq(candidate.Position, entry.Position) > 10000f)
                    {
                        continue;
                    }
                    result.Add(candidate);
                }
            }
            return result;
        }

        private void RebuildComponent(List<Entry> component)
        {
            if (component == null || component.Count == 0)
                return;

            var entries = new List<Entry>();
            for (int i = 0; i < component.Count; i++)
            {
                Entry entry = component[i];
                if (entry != null
                    && m_Entries.TryGetValue(entry.Key, out Entry current)
                    && ReferenceEquals(entry, current)
                    && !entries.Contains(entry))
                {
                    entries.Add(entry);
                }
            }
            entries.Sort(CompareEntries);

            var groups = new List<List<Entry>>();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                List<Entry> selected = null;
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    if (Fits(groups[groupIndex], entry))
                    {
                        selected = groups[groupIndex];
                        break;
                    }
                }
                if (selected == null)
                {
                    selected = new List<Entry>();
                    groups.Add(selected);
                }
                selected.Add(entry);
            }

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                List<Entry> group = groups[groupIndex];
                group.Sort(CompareEntries);
                string groupId = group[0].StopKey;
                for (int entryIndex = 0; entryIndex < group.Count; entryIndex++)
                {
                    Entry entry = group[entryIndex];
                    if (string.Equals(entry.GroupId, groupId, StringComparison.Ordinal))
                        continue;
                    entry.GroupId = groupId;
                    MarkLinesForLane(entry.Lane);
                }
            }
        }

        private static bool Fits(List<Entry> group, Entry entry)
        {
            for (int i = 0; i < group.Count; i++)
                if (math.distancesq(group[i].Position, entry.Position) > 10000f)
                    return false;
            return true;
        }

        private static int CompareEntries(Entry left, Entry right)
        {
            int result = StringComparer.Ordinal.Compare(left.StopKey, right.StopKey);
            if (result != 0)
                return result;
            result = left.Line.Index.CompareTo(right.Line.Index);
            if (result != 0)
                return result;
            result = left.Line.Version.CompareTo(right.Line.Version);
            return result != 0
                ? result
                : left.WaypointIndex.CompareTo(right.WaypointIndex);
        }

        private static SpatialKey Cell(float3 position) => new SpatialKey(
            (int)math.floor(position.x / 100f),
            (int)math.floor(position.y / 100f),
            (int)math.floor(position.z / 100f));

        private static void AddIndexed<TKey>(Dictionary<TKey, List<Entry>> index, TKey key, Entry entry)
        {
            if (!index.TryGetValue(key, out List<Entry> entries))
            {
                entries = new List<Entry>();
                index[key] = entries;
            }
            entries.Add(entry);
        }

        private static void RemoveIndexed<TKey>(Dictionary<TKey, List<Entry>> index, TKey key, Entry entry)
        {
            if (!index.TryGetValue(key, out List<Entry> entries))
                return;
            entries.RemoveAll(value => value.Key.Equals(entry.Key));
            if (entries.Count == 0)
                index.Remove(key);
        }

        private static bool SameFingerprint(Entry left, Entry right)
        {
            return left != null && right != null
                && left.Stop == right.Stop
                && left.Waypoint == right.Waypoint
                && left.WaypointIndex == right.WaypointIndex
                && left.Lane == right.Lane
                && math.abs(left.CurvePosition - right.CurvePosition) <= 0.0001f
                && left.PositionEntity == right.PositionEntity
                && math.distancesq(left.Position, right.Position) <= 0.0001f
                && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                && string.Equals(left.StopKey, right.StopKey, StringComparison.Ordinal);
        }

        private void MarkDirty(Entity line)
        {
            if (line != Entity.Null && m_DirtySet.Add(line))
                m_DirtyLines.Enqueue(line);
        }

        private void RegisterChainLanes(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null || chain == null)
                return;

            var lanes = new List<Entity>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                Entity lane = chain.TrackAtoms[atomIndex].Key.PhysicalLaneKey;
                if (lane == Entity.Null || lanes.Contains(lane))
                    continue;
                lanes.Add(lane);
                if (!m_LinesByLane.TryGetValue(lane, out HashSet<Entity> lines))
                {
                    lines = new HashSet<Entity>();
                    m_LinesByLane[lane] = lines;
                }
                lines.Add(line);
            }
            if (lanes.Count > 0)
                RegisterKnownLanes(line, lanes);
        }

        private void RegisterKnownLanes(Entity line, List<Entity> lanes)
        {
            if (line == Entity.Null || lanes == null || lanes.Count == 0)
                return;
            m_LanesByLine[line] = lanes;
            for (int i = 0; i < lanes.Count; i++)
            {
                Entity lane = lanes[i];
                if (!m_LinesByLane.TryGetValue(lane, out HashSet<Entity> lines))
                {
                    lines = new HashSet<Entity>();
                    m_LinesByLane[lane] = lines;
                }
                lines.Add(line);
            }
        }

        private void MarkLinesForLane(Entity lane)
        {
            if (lane == Entity.Null || !m_LinesByLane.TryGetValue(lane, out HashSet<Entity> lines))
                return;
            foreach (Entity line in lines)
                MarkDirty(line);
        }

        private sealed class Entry
        {
            internal EntryKey Key;
            internal Entity Line;
            internal Entity Stop;
            internal Entity Waypoint;
            internal int WaypointIndex;
            internal Entity Lane;
            internal float CurvePosition;
            internal string Name;
            internal string StopKey;
            internal Entity PositionEntity;
            internal float3 Position;
            internal string GroupId;
        }

        private readonly struct EntryKey : IEquatable<EntryKey>
        {
            private readonly Entity m_Line;
            private readonly int m_WaypointIndex;

            internal EntryKey(Entity line, int waypointIndex)
            {
                m_Line = line;
                m_WaypointIndex = waypointIndex;
            }

            public bool Equals(EntryKey other) =>
                m_Line == other.m_Line && m_WaypointIndex == other.m_WaypointIndex;

            public override bool Equals(object obj) => obj is EntryKey other && Equals(other);

            public override int GetHashCode() => unchecked(m_Line.GetHashCode() * 397 ^ m_WaypointIndex);
        }

        private readonly struct SpatialKey : IEquatable<SpatialKey>
        {
            internal readonly int X;
            internal readonly int Y;
            internal readonly int Z;

            internal SpatialKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(SpatialKey other) =>
                X == other.X && Y == other.Y && Z == other.Z;

            public override bool Equals(object obj) =>
                obj is SpatialKey other && Equals(other);

            public override int GetHashCode() => unchecked(
                ((X * 397) ^ Y) * 397 ^ Z);
        }
    }
}
