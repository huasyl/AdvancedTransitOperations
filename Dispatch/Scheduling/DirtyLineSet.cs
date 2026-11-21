using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class DirtyLineSet
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly HashSet<string> m_CurrentDirtyLineKeys = new HashSet<string>();
        private readonly HashSet<string> m_PendingDirtyLineKeys = new HashSet<string>();
        private readonly List<Entity> m_ResolvedDirtyLines = new List<Entity>();
        private bool m_AllLinesDirty;
        private bool m_PendingAllLinesDirty;
        private bool m_Sealed;

        public DirtyLineSet(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal IReadOnlyList<Entity> ResolvedDirtyLines => m_ResolvedDirtyLines;

        internal void BeginFrame()
        {
            m_Sealed = false;
            m_CurrentDirtyLineKeys.Clear();
            m_CurrentDirtyLineKeys.UnionWith(m_PendingDirtyLineKeys);
            m_PendingDirtyLineKeys.Clear();
            m_AllLinesDirty = m_PendingAllLinesDirty;
            m_PendingAllLinesDirty = false;
            m_ResolvedDirtyLines.Clear();
        }

        internal void MarkDirty(Entity line)
        {
            if (line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && m_Runtime.m_LineView.TryFrame(line, out LineFrame frame))
            {
                if (m_Sealed)
                    m_PendingDirtyLineKeys.Add(frame.StoreKey.ToString());
                else
                    m_CurrentDirtyLineKeys.Add(frame.StoreKey.ToString());
                return;
            }
            MarkAllDirty();
        }

        internal void MarkAllDirty()
        {
            if (m_Sealed)
                m_PendingAllLinesDirty = true;
            else
                m_AllLinesDirty = true;
        }

        internal void MarkPendingDirty(string lineKey)
        {
            if (!string.IsNullOrEmpty(lineKey))
                m_PendingDirtyLineKeys.Add(lineKey);
        }

        internal void MarkPendingAllDirty() => m_PendingAllLinesDirty = true;

        internal void SealDirtyLines()
        {
            m_Sealed = true;
            m_ResolvedDirtyLines.Clear();
            if (!m_AllLinesDirty && m_CurrentDirtyLineKeys.Count == 0)
                return;

            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!m_AllLinesDirty
                        && (!m_Runtime.m_LineView.TryFrame(line, out LineFrame frame)
                            || !m_CurrentDirtyLineKeys.Contains(frame.StoreKey.ToString())))
                    {
                        continue;
                    }
                    m_ResolvedDirtyLines.Add(line);
                }
            }
            finally
            {
                lines.Dispose();
            }
            m_ResolvedDirtyLines.Sort(CompareEntity);
        }

        internal void ResetCity()
        {
            m_CurrentDirtyLineKeys.Clear();
            m_PendingDirtyLineKeys.Clear();
            m_ResolvedDirtyLines.Clear();
            m_AllLinesDirty = false;
            m_PendingAllLinesDirty = false;
            m_Sealed = false;
        }

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }
    }
}
