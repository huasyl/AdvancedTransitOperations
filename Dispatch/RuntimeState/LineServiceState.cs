using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal enum LineServiceChangeKind : byte
    {
        Paused,
        Resumed
    }

    internal enum LineServiceGate : byte
    {
        Unknown,
        WaitingStable,
        Operational,
        Paused
    }

    internal readonly struct LineServiceChange
    {
        internal readonly Entity Line;
        internal readonly bool Operational;
        internal readonly LineServiceChangeKind Kind;

        internal LineServiceChange(Entity line, bool operational)
        {
            Line = line;
            Operational = operational;
            Kind = operational ? LineServiceChangeKind.Resumed : LineServiceChangeKind.Paused;
        }
    }

    internal sealed class LineServiceState
    {
        private readonly EntityManager m_EntityManager;
        private readonly Dictionary<Entity, bool> m_ObservedOperational = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, bool> m_AppliedOperational = new Dictionary<Entity, bool>();
        private readonly HashSet<Entity> m_DirtyLines = new HashSet<Entity>();
        private readonly HashSet<Entity> m_PendingRefresh = new HashSet<Entity>();
        private readonly List<Entity> m_DrainBuffer = new List<Entity>();

        internal LineServiceState(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        internal LineServiceGate GetGate(Entity line)
        {
            if (line == Entity.Null)
                return LineServiceGate.Unknown;
            if (m_PendingRefresh.Contains(line))
                return LineServiceGate.WaitingStable;
            return m_ObservedOperational.TryGetValue(line, out bool operational)
                ? operational ? LineServiceGate.Operational : LineServiceGate.Paused
                : LineServiceGate.Unknown;
        }

        internal bool IsOperational(Entity line)
        {
            return GetGate(line) == LineServiceGate.Operational;
        }

        internal void Observe(Entity line)
        {
            if (line == Entity.Null)
                return;
            if (!m_ObservedOperational.ContainsKey(line))
            {
                m_PendingRefresh.Add(line);
                return;
            }

            bool operational = ComputeOperational(line);
            m_ObservedOperational[line] = operational;
            if (m_AppliedOperational.TryGetValue(line, out bool applied)
                && operational != applied)
            {
                m_DirtyLines.Add(line);
            }
        }

        internal void EnsureObserved(Entity line)
        {
            if (line == Entity.Null)
                return;
            if (m_ObservedOperational.ContainsKey(line) && !m_PendingRefresh.Contains(line))
                return;

            bool operational = ComputeOperational(line);
            bool hadApplied = m_AppliedOperational.TryGetValue(line, out bool applied);
            m_ObservedOperational[line] = operational;
            m_PendingRefresh.Remove(line);
            if (!hadApplied)
            {
                m_AppliedOperational[line] = operational ? operational : true;
                if (!operational)
                    m_DirtyLines.Add(line);
                return;
            }

            if (operational != applied)
                m_DirtyLines.Add(line);
        }

        internal void BaselineStableLines(IReadOnlyList<Entity> lines)
        {
            if (lines == null)
                return;
            for (int i = 0; i < lines.Count; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null)
                    continue;
                bool operational = ComputeOperational(line);
                m_ObservedOperational[line] = operational;
                m_AppliedOperational[line] = operational;
                m_PendingRefresh.Remove(line);
                m_DirtyLines.Remove(line);
            }
        }

        internal void DrainChanges(List<LineServiceChange> output)
        {
            if (output == null)
                return;
            output.Clear();
            if (m_DirtyLines.Count == 0)
                return;

            m_DrainBuffer.Clear();
            foreach (Entity line in m_DirtyLines)
                m_DrainBuffer.Add(line);
            for (int i = 0; i < m_DrainBuffer.Count; i++)
            {
                Entity line = m_DrainBuffer[i];
                if (!m_ObservedOperational.TryGetValue(line, out bool observed)
                    || !m_AppliedOperational.TryGetValue(line, out bool applied)
                    || observed == applied)
                {
                    m_DirtyLines.Remove(line);
                    continue;
                }
                output.Add(new LineServiceChange(line, observed));
            }
            m_DrainBuffer.Clear();
        }

        internal void MarkApplied(Entity line, bool operational)
        {
            if (line == Entity.Null)
                return;
            m_AppliedOperational[line] = operational;
            if (!m_ObservedOperational.TryGetValue(line, out bool observed)
                || observed == operational)
            {
                m_DirtyLines.Remove(line);
            }
            else
            {
                m_DirtyLines.Add(line);
            }
        }

        internal void Forget(Entity line)
        {
            if (line == Entity.Null)
                return;
            m_ObservedOperational.Remove(line);
            m_AppliedOperational.Remove(line);
            m_DirtyLines.Remove(line);
            m_PendingRefresh.Remove(line);
        }

        internal void Reset()
        {
            m_ObservedOperational.Clear();
            m_AppliedOperational.Clear();
            m_DirtyLines.Clear();
            m_PendingRefresh.Clear();
            m_DrainBuffer.Clear();
        }

        private bool ComputeOperational(Entity line)
        {
            if (line == Entity.Null
                || !m_EntityManager.Exists(line)
                || m_EntityManager.HasComponent<Deleted>(line)
                || m_EntityManager.HasComponent<Temp>(line)
                || m_EntityManager.HasComponent<Disabled>(line)
                || !m_EntityManager.HasComponent<Route>(line))
            {
                return false;
            }

            Route route = m_EntityManager.GetComponentData<Route>(line);
            return !RouteUtils.CheckOption(route, RouteOption.Inactive);
        }
    }
}
