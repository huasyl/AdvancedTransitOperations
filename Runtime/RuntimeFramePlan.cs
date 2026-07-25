using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Runtime
{
    internal sealed class RuntimeFramePlan : IDisposable
    {
        private readonly List<FramePlanEntry> m_Entries = new List<FramePlanEntry>();
        private readonly Dictionary<Entity, int> m_EntryIndex = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, RuntimeStageMask> m_PendingStages = new Dictionary<Entity, RuntimeStageMask>();
        private readonly List<FramePlanEntry> m_FrozenEntries = new List<FramePlanEntry>();
        private readonly List<DeadlineEntry> m_DueDeadlines = new List<DeadlineEntry>();
        private readonly Dictionary<DeadlineKey, DeadlineSlot> m_Deadlines = new Dictionary<DeadlineKey, DeadlineSlot>();
        private readonly List<DeadlineTicket> m_DeadlineHeap = new List<DeadlineTicket>(256);
        private readonly List<DeadlineKey> m_DeadlineScratch = new List<DeadlineKey>(64);
        private readonly List<UiCommand> m_FrameUiCommands = new List<UiCommand>();
        private readonly List<UiCommand> m_PendingUiCommands = new List<UiCommand>();
        private RuntimeStageMask m_FrozenStages;
        private RuntimeStageMask m_CurrentFrozenStage;
        private uint m_NextDeadlineVersion;

        private readonly struct DeadlineSlot
        {
            public readonly uint DueFrame;
            public readonly uint Version;

            public DeadlineSlot(uint dueFrame, uint version)
            {
                DueFrame = dueFrame;
                Version = version;
            }
        }

        private readonly struct DeadlineTicket
        {
            public readonly DeadlineKey Key;
            public readonly uint DueFrame;
            public readonly uint Version;

            public DeadlineTicket(DeadlineKey key, uint dueFrame, uint version)
            {
                Key = key;
                DueFrame = dueFrame;
                Version = version;
            }
        }

        public IReadOnlyList<FramePlanEntry> Entries => m_Entries;
        public IReadOnlyList<DeadlineEntry> DueDeadlines => m_DueDeadlines;
        public IReadOnlyList<UiCommand> UiCommands => m_FrameUiCommands;

        public void BeginFrame()
        {
            m_Entries.Clear();
            m_EntryIndex.Clear();
            m_FrozenEntries.Clear();
            m_FrozenStages = RuntimeStageMask.None;
            m_CurrentFrozenStage = RuntimeStageMask.None;
            m_DueDeadlines.Clear();
            m_FrameUiCommands.Clear();

            foreach (KeyValuePair<Entity, RuntimeStageMask> pending in m_PendingStages)
                AddStage(pending.Key, -1, pending.Value);
            m_PendingStages.Clear();
        }

        public void AddStage(Entity vehicle, RuntimeStageMask stage, string debugReason = null)
        {
            AddStage(vehicle, -1, stage, debugReason);
        }

        public void AddStage(Entity vehicle, int sourceRowIndex, RuntimeStageMask stages, string debugReason = null)
        {
            if (vehicle == Entity.Null || stages == RuntimeStageMask.None)
                return;

            RuntimeStageMask frozen = stages & m_FrozenStages;
            if (frozen != RuntimeStageMask.None)
            {
                m_PendingStages[vehicle] = m_PendingStages.TryGetValue(vehicle, out RuntimeStageMask existing)
                    ? existing | frozen
                    : frozen;
                stages &= ~m_FrozenStages;
            }

            if (stages == RuntimeStageMask.None)
                return;

            if (!m_EntryIndex.TryGetValue(vehicle, out int index))
            {
                m_EntryIndex.Add(vehicle, m_Entries.Count);
                m_Entries.Add(new FramePlanEntry(vehicle, sourceRowIndex, stages));
                return;
            }

            FramePlanEntry current = m_Entries[index];
            int currentSourceRow = current.SourceRowIndex >= 0 ? current.SourceRowIndex : sourceRowIndex;
            m_Entries[index] = new FramePlanEntry(current.Vehicle, currentSourceRow, current.Stages | stages);
        }

        public void Freeze(RuntimeStageMask stage)
        {
            if (stage == RuntimeStageMask.None || (stage & (stage - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(stage));

            m_FrozenEntries.Clear();
            for (int i = 0; i < m_Entries.Count; i++)
            {
                FramePlanEntry entry = m_Entries[i];
                if ((entry.Stages & stage) != 0)
                    m_FrozenEntries.Add(entry);
            }

            m_FrozenStages |= stage;
            m_CurrentFrozenStage = stage;
        }

        public IReadOnlyList<FramePlanEntry> ForStage(RuntimeStageMask stage)
        {
            if (m_CurrentFrozenStage != stage)
                throw new InvalidOperationException("阶段必须先冻结后读取。");
            return m_FrozenEntries;
        }

        public void CollectDueDeadlines(uint nowFrame)
        {
            while (m_DeadlineHeap.Count > 0)
            {
                DeadlineTicket ticket = m_DeadlineHeap[0];
                if (nowFrame < ticket.DueFrame)
                    return;

                PopDeadline();
                if (!m_Deadlines.TryGetValue(ticket.Key, out DeadlineSlot stored)
                    || stored.DueFrame != ticket.DueFrame
                    || stored.Version != ticket.Version)
                    continue;

                m_Deadlines.Remove(ticket.Key);
                m_DueDeadlines.Add(new DeadlineEntry(ticket.Key.Vehicle, ticket.Key.Kind, ticket.DueFrame));
            }
        }

        public void SetDeadline(Entity vehicle, DeadlineKind kind, uint dueFrame)
        {
            if (vehicle == Entity.Null)
                return;

            DeadlineKey key = new DeadlineKey(vehicle, kind);
            RemoveDueDeadline(vehicle, kind);
            uint version = unchecked(++m_NextDeadlineVersion);
            m_Deadlines[key] = new DeadlineSlot(dueFrame, version);
            PushDeadline(new DeadlineTicket(key, dueFrame, version));
        }

        public void ClearDeadline(Entity vehicle, DeadlineKind kind)
        {
            if (vehicle == Entity.Null)
                return;

            DeadlineKey key = new DeadlineKey(vehicle, kind);
            if (m_Deadlines.ContainsKey(key))
                m_Deadlines.Remove(key);
            RemoveDueDeadline(vehicle, kind);
        }

        public void ClearDeadlines(DeadlineKind kind)
        {
            m_DeadlineScratch.Clear();
            foreach (DeadlineKey key in m_Deadlines.Keys)
            {
                if (key.Kind == kind)
                    m_DeadlineScratch.Add(key);
            }
            for (int i = 0; i < m_DeadlineScratch.Count; i++)
                ClearDeadline(m_DeadlineScratch[i].Vehicle, m_DeadlineScratch[i].Kind);
            m_DeadlineScratch.Clear();
            RemoveDueDeadlines(kind);
        }

        public bool TryGetDeadline(Entity vehicle, DeadlineKind kind, out uint dueFrame)
        {
            if (vehicle != Entity.Null && m_Deadlines.TryGetValue(new DeadlineKey(vehicle, kind), out DeadlineSlot stored))
            {
                dueFrame = stored.DueFrame;
                return true;
            }

            for (int i = 0; i < m_DueDeadlines.Count; i++)
            {
                DeadlineEntry entry = m_DueDeadlines[i];
                if (entry.Vehicle == vehicle && entry.Kind == kind)
                {
                    dueFrame = entry.DueFrame;
                    return true;
                }
            }

            dueFrame = 0;
            return false;
        }

        public bool IsDeadlineDue(Entity vehicle, DeadlineKind kind, uint nowFrame)
        {
            return TryGetDeadline(vehicle, kind, out uint dueFrame) && nowFrame >= dueFrame;
        }

        public void ClearVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            if (m_EntryIndex.TryGetValue(vehicle, out int index))
            {
                FramePlanEntry entry = m_Entries[index];
                m_Entries[index] = new FramePlanEntry(entry.Vehicle, entry.SourceRowIndex, RuntimeStageMask.None);
            }
            m_PendingStages.Remove(vehicle);

            m_DeadlineScratch.Clear();
            foreach (DeadlineKey key in m_Deadlines.Keys)
            {
                if (key.Vehicle == vehicle)
                    m_DeadlineScratch.Add(key);
            }
            for (int i = 0; i < m_DeadlineScratch.Count; i++)
                ClearDeadline(m_DeadlineScratch[i].Vehicle, m_DeadlineScratch[i].Kind);
            m_DeadlineScratch.Clear();
            RemoveDueDeadlines(vehicle);
        }

        public void EnqueueUiCommand(RetireCommand command) => EnqueueUiCommand(UiCommandKind.Retire, command.Vehicle);
        public void EnqueueUiCommand(RecheckCommand command) => EnqueueUiCommand(UiCommandKind.Recheck, command.Vehicle);
        public void EnqueueUiCommand(DepartCommand command) => EnqueueUiCommand(UiCommandKind.Depart, command.Vehicle);
        public void EnqueueUiCommand(SpawnCommand command) => EnqueueUiCommand(UiCommandKind.Spawn, command.Line);

        public void DrainUiCommands()
        {
            m_FrameUiCommands.AddRange(m_PendingUiCommands);
            m_PendingUiCommands.Clear();
        }

        public void ResetCity()
        {
            m_Entries.Clear();
            m_EntryIndex.Clear();
            m_PendingStages.Clear();
            m_FrozenEntries.Clear();
            m_DueDeadlines.Clear();
            m_Deadlines.Clear();
            m_DeadlineHeap.Clear();
            m_DeadlineScratch.Clear();
            m_FrameUiCommands.Clear();
            m_PendingUiCommands.Clear();
            m_FrozenStages = RuntimeStageMask.None;
            m_CurrentFrozenStage = RuntimeStageMask.None;
            m_NextDeadlineVersion = 0;
        }

        public void Dispose() => ResetCity();

        private void EnqueueUiCommand(UiCommandKind kind, Entity entity)
        {
            if (entity != Entity.Null)
                m_PendingUiCommands.Add(new UiCommand(kind, entity));
        }

        private void RemoveDueDeadline(Entity vehicle, DeadlineKind kind)
        {
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                DeadlineEntry entry = m_DueDeadlines[i];
                if (entry.Vehicle == vehicle && entry.Kind == kind)
                    m_DueDeadlines.RemoveAt(i);
            }
        }

        private void RemoveDueDeadlines(DeadlineKind kind)
        {
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                if (m_DueDeadlines[i].Kind == kind)
                    m_DueDeadlines.RemoveAt(i);
            }
        }

        private void RemoveDueDeadlines(Entity vehicle)
        {
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                if (m_DueDeadlines[i].Vehicle == vehicle)
                    m_DueDeadlines.RemoveAt(i);
            }
        }

        private void PushDeadline(DeadlineTicket ticket)
        {
            int index = m_DeadlineHeap.Count;
            m_DeadlineHeap.Add(ticket);
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (!Earlier(ticket, m_DeadlineHeap[parent]))
                    break;

                m_DeadlineHeap[index] = m_DeadlineHeap[parent];
                index = parent;
            }
            m_DeadlineHeap[index] = ticket;
        }

        private void PopDeadline()
        {
            int lastIndex = m_DeadlineHeap.Count - 1;
            DeadlineTicket last = m_DeadlineHeap[lastIndex];
            m_DeadlineHeap.RemoveAt(lastIndex);
            if (lastIndex == 0)
                return;

            int index = 0;
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= lastIndex)
                    break;

                int right = left + 1;
                int child = right < lastIndex && Earlier(m_DeadlineHeap[right], m_DeadlineHeap[left])
                    ? right
                    : left;
                if (!Earlier(m_DeadlineHeap[child], last))
                    break;

                m_DeadlineHeap[index] = m_DeadlineHeap[child];
                index = child;
            }
            m_DeadlineHeap[index] = last;
        }

        private static bool Earlier(DeadlineTicket left, DeadlineTicket right)
        {
            if (left.DueFrame != right.DueFrame)
                return left.DueFrame < right.DueFrame;
            if (left.Key.Vehicle.Index != right.Key.Vehicle.Index)
                return left.Key.Vehicle.Index < right.Key.Vehicle.Index;
            if (left.Key.Vehicle.Version != right.Key.Vehicle.Version)
                return left.Key.Vehicle.Version < right.Key.Vehicle.Version;
            if (left.Key.Kind != right.Key.Kind)
                return left.Key.Kind < right.Key.Kind;
            return left.Version < right.Version;
        }
    }
}
