using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum DeadlineKind : byte
    {
        Dwell, Ready, Idle, LaunchCooldown, PreparingCooldown, OriginBoardingGrace, ForcedMidStopBoardingGrace, BvMisfire, OriginSettle,
        RetireBoundary, RetireHardAck, RescueProbe, RescueStall, RescueRecheck,
        SliceSample, SliceEntryProbe, SliceRefresh
    }

    internal readonly struct DeadlineKey : IEquatable<DeadlineKey>
    {
        public readonly Entity Vehicle;
        public readonly DeadlineKind Kind;
        public DeadlineKey(Entity vehicle, DeadlineKind kind) { Vehicle = vehicle; Kind = kind; }
        public bool Equals(DeadlineKey other) => Vehicle == other.Vehicle && Kind == other.Kind;
        public override bool Equals(object obj) => obj is DeadlineKey other && Equals(other);
        public override int GetHashCode() => (Vehicle.GetHashCode() * 397) ^ (int)Kind;
    }

    internal readonly struct DeadlineEntry
    {
        public readonly Entity Vehicle;
        public readonly DeadlineKind Kind;
        public readonly uint DueFrame;
        public DeadlineEntry(Entity vehicle, DeadlineKind kind, uint dueFrame) { Vehicle = vehicle; Kind = kind; DueFrame = dueFrame; }
    }

    internal sealed class RuntimeWorksets : IDisposable
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly FrameEvents m_Events;

        // 本帧数据：BeginFrame 清候选、到期、脏线路和去重表，再导入 pending。
        private readonly HashSet<Entity> m_CurrentCandidates = new HashSet<Entity>();
        private readonly HashSet<string> m_CurrentDirtyLineKeys = new HashSet<string>();
        private readonly List<Entity> m_FrozenVehicles = new List<Entity>();
        private readonly List<DeadlineEntry> m_DueDeadlines = new List<DeadlineEntry>();
        private readonly List<Entity> m_RescueExpressCandidates = new List<Entity>();
        private readonly HashSet<Entity> m_RescueExpressCandidateSet = new HashSet<Entity>();
        private readonly List<Entity> m_ResolvedDirtyLines = new List<Entity>();
        private bool m_Built;
        private bool m_Sealed;
        private bool m_AllLinesDirty;

        // 跨帧数据：期限、活跃 owner 和 pending 不会被 BeginFrame 清掉。
        private readonly Dictionary<DeadlineKey, uint> m_Deadlines = new Dictionary<DeadlineKey, uint>();
        private readonly HashSet<Entity> m_ActiveBypass = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ActiveRetire = new HashSet<Entity>();
        private readonly HashSet<Entity> m_PendingVehicleCandidates = new HashSet<Entity>();
        private readonly HashSet<string> m_PendingDirtyLineKeys = new HashSet<string>();
        private bool m_PendingAllLinesDirty;

        public RuntimeWorksets(ModRuntimeHostSystem runtime, FrameEvents events) { m_Runtime = runtime; m_Events = events; }
        public IReadOnlyList<Entity> FrozenVehicles => m_FrozenVehicles;
        public IReadOnlyList<DeadlineEntry> DueDeadlines => m_DueDeadlines;
        public IReadOnlyList<Entity> RescueExpressCandidates => m_RescueExpressCandidates;
        public IReadOnlyCollection<Entity> ActiveBypass => m_ActiveBypass;
        public IReadOnlyCollection<Entity> ActiveRetire => m_ActiveRetire;
        public bool AllLinesDirty => m_AllLinesDirty;
        public IReadOnlyList<Entity> ResolvedDirtyLines => m_ResolvedDirtyLines;

        public void BeginFrame()
        {
            // 仅清本帧视图并导入 pending；期限、活跃集合和未执行命令保持跨帧。
            m_CurrentCandidates.Clear();
            m_CurrentDirtyLineKeys.Clear();
            m_FrozenVehicles.Clear();
            m_DueDeadlines.Clear();
            m_RescueExpressCandidates.Clear();
            m_RescueExpressCandidateSet.Clear();
            m_ResolvedDirtyLines.Clear();
            m_Built = false;
            m_Sealed = false;
            m_AllLinesDirty = m_PendingAllLinesDirty;
            m_PendingAllLinesDirty = false;
            m_CurrentCandidates.UnionWith(m_PendingVehicleCandidates);
            m_CurrentDirtyLineKeys.UnionWith(m_PendingDirtyLineKeys);
            m_PendingVehicleCandidates.Clear();
            m_PendingDirtyLineKeys.Clear();
        }

        public void Build()
        {
            if (m_Built) return;
            for (int i = 0; i < m_Events.VehicleEvents.Count; i++) AddCandidate(m_Events.VehicleEvents[i].Vehicle);
            for (int i = 0; i < m_Events.DispatchEvents.Count; i++)
            {
                DispatchEvent dispatchEvent = m_Events.DispatchEvents[i];
                AddCandidate(dispatchEvent.Vehicle);
                MarkDirty(dispatchEvent.Line);
            }
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            foreach (KeyValuePair<DeadlineKey, uint> entry in m_Deadlines)
            {
                if (nowFrame < entry.Value) continue;
                m_DueDeadlines.Add(new DeadlineEntry(entry.Key.Vehicle, entry.Key.Kind, entry.Value));
                AddCandidate(entry.Key.Vehicle);
                if (entry.Key.Kind == DeadlineKind.RescueProbe
                    || entry.Key.Kind == DeadlineKind.RescueStall
                    || entry.Key.Kind == DeadlineKind.RescueRecheck)
                {
                    m_RescueExpressCandidateSet.Add(entry.Key.Vehicle);
                }
            }
            foreach (Entity vehicle in m_ActiveBypass) AddCandidate(vehicle);
            foreach (Entity vehicle in m_ActiveRetire) AddCandidate(vehicle);
            foreach (Entity vehicle in m_CurrentCandidates)
            {
                if (vehicle != Entity.Null && m_Runtime.EntityManager.Exists(vehicle) && m_Runtime.m_VehicleView.Contains(vehicle))
                    m_FrozenVehicles.Add(vehicle);
            }
            m_FrozenVehicles.Sort(CompareEntity);
            m_DueDeadlines.Sort(CompareDeadline);
            foreach (Entity vehicle in m_RescueExpressCandidateSet)
            {
                if (vehicle != Entity.Null
                    && m_Runtime.EntityManager.Exists(vehicle)
                    && m_Runtime.m_VehicleView.Contains(vehicle)
                    && m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line)
                    && line != Entity.Null
                    && m_Runtime.EntityManager.Exists(line))
                {
                    m_RescueExpressCandidates.Add(vehicle);
                }
            }
            m_RescueExpressCandidates.Sort(CompareRescueCandidate);
            m_Built = true;
        }

        public IReadOnlyCollection<string> SealDirtyLines()
        {
            m_Sealed = true;
            ResolveDirtyLines();
            return new List<string>(m_CurrentDirtyLineKeys);
        }

        public void AddCandidate(Entity vehicle)
        {
            if (vehicle == Entity.Null) return;
            (m_Built ? m_PendingVehicleCandidates : m_CurrentCandidates).Add(vehicle);
        }

        public void MarkDirty(string lineKey)
        {
            if (string.IsNullOrEmpty(lineKey)) return;
            (m_Sealed ? m_PendingDirtyLineKeys : m_CurrentDirtyLineKeys).Add(lineKey);
        }

        public void MarkDirty(Entity line)
        {
            if (line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && m_Runtime.m_LineView.TryFrame(line, out LineFrame frame))
            {
                MarkDirty(frame.StoreKey.ToString());
                return;
            }

            MarkAllDirty();
        }

        public void MarkAllDirty()
        {
            if (m_Sealed) m_PendingAllLinesDirty = true;
            else m_AllLinesDirty = true;
        }

        public void MarkPendingDirty(string lineKey)
        {
            if (!string.IsNullOrEmpty(lineKey)) m_PendingDirtyLineKeys.Add(lineKey);
        }

        public void MarkPendingAllDirty() => m_PendingAllLinesDirty = true;

        public void SetDeadline(Entity vehicle, DeadlineKind kind, uint dueFrame)
        {
            if (vehicle == Entity.Null) return;
            RemoveDueDeadline(vehicle, kind);
            m_Deadlines[new DeadlineKey(vehicle, kind)] = dueFrame;
        }

        public void ClearDeadline(Entity vehicle, DeadlineKind kind)
        {
            if (vehicle == Entity.Null) return;
            m_Deadlines.Remove(new DeadlineKey(vehicle, kind));
            RemoveDueDeadline(vehicle, kind);
        }

        public void ClearDeadlines(DeadlineKind kind)
        {
            List<DeadlineKey> stale = new List<DeadlineKey>();
            foreach (DeadlineKey key in m_Deadlines.Keys) if (key.Kind == kind) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) m_Deadlines.Remove(stale[i]);
            HashSet<Entity> dueVehicles = new HashSet<Entity>();
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                if (m_DueDeadlines[i].Kind != kind) continue;
                dueVehicles.Add(m_DueDeadlines[i].Vehicle);
                m_DueDeadlines.RemoveAt(i);
            }
            if (IsRescueDeadline(kind))
            {
                foreach (Entity vehicle in dueVehicles)
                {
                    if (!HasDueRescue(vehicle))
                        RemoveRescueCandidate(vehicle);
                }
            }
        }

        public void ClearVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null) return;
            m_ActiveBypass.Remove(vehicle);
            m_ActiveRetire.Remove(vehicle);
            m_PendingVehicleCandidates.Remove(vehicle);
            List<DeadlineKey> stale = new List<DeadlineKey>();
            foreach (DeadlineKey key in m_Deadlines.Keys) if (key.Vehicle == vehicle) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) m_Deadlines.Remove(stale[i]);
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                if (m_DueDeadlines[i].Vehicle == vehicle)
                    m_DueDeadlines.RemoveAt(i);
            }
            RemoveRescueCandidate(vehicle);
        }

        public void SetBypassActive(Entity vehicle, bool active) { if (active) m_ActiveBypass.Add(vehicle); else m_ActiveBypass.Remove(vehicle); }
        public void ClearActiveBypass() => m_ActiveBypass.Clear();
        public void SetRetireActive(Entity vehicle, bool active) { if (active) m_ActiveRetire.Add(vehicle); else m_ActiveRetire.Remove(vehicle); }
        public void ClearActiveRetire() => m_ActiveRetire.Clear();

        public void ResetCity()
        {
            // 整体清理本帧视图、期限、活跃集合和全部 pending。
            m_CurrentCandidates.Clear(); m_CurrentDirtyLineKeys.Clear(); m_FrozenVehicles.Clear(); m_DueDeadlines.Clear(); m_RescueExpressCandidates.Clear(); m_RescueExpressCandidateSet.Clear(); m_ResolvedDirtyLines.Clear();
            m_Deadlines.Clear(); m_ActiveBypass.Clear(); m_ActiveRetire.Clear(); m_PendingVehicleCandidates.Clear(); m_PendingDirtyLineKeys.Clear();
            m_AllLinesDirty = false; m_PendingAllLinesDirty = false; m_Built = false; m_Sealed = false;
        }

        // 销毁不保留任何托管集合。
        public void Dispose() => ResetCity();
        private static int CompareEntity(Entity left, Entity right) => left.Index != right.Index ? left.Index.CompareTo(right.Index) : left.Version.CompareTo(right.Version);
        private static int CompareDeadline(DeadlineEntry left, DeadlineEntry right)
        {
            int vehicleOrder = CompareEntity(left.Vehicle, right.Vehicle);
            return vehicleOrder != 0 ? vehicleOrder : left.Kind.CompareTo(right.Kind);
        }

        private void RemoveDueDeadline(Entity vehicle, DeadlineKind kind)
        {
            for (int i = m_DueDeadlines.Count - 1; i >= 0; i--)
            {
                DeadlineEntry entry = m_DueDeadlines[i];
                if (entry.Vehicle == vehicle && entry.Kind == kind)
                    m_DueDeadlines.RemoveAt(i);
            }
            if (IsRescueDeadline(kind) && !HasDueRescue(vehicle))
                RemoveRescueCandidate(vehicle);
        }

        private bool HasDueRescue(Entity vehicle)
        {
            for (int i = 0; i < m_DueDeadlines.Count; i++)
            {
                DeadlineEntry entry = m_DueDeadlines[i];
                if (entry.Vehicle == vehicle && IsRescueDeadline(entry.Kind))
                    return true;
            }
            return false;
        }

        private void RemoveRescueCandidate(Entity vehicle)
        {
            m_RescueExpressCandidateSet.Remove(vehicle);
            m_RescueExpressCandidates.Remove(vehicle);
        }

        private static bool IsRescueDeadline(DeadlineKind kind)
        {
            return kind == DeadlineKind.RescueProbe
                || kind == DeadlineKind.RescueStall
                || kind == DeadlineKind.RescueRecheck;
        }

        private int CompareRescueCandidate(Entity left, Entity right)
        {
            m_Runtime.m_VehicleView.TryGetLine(left, out Entity leftLine);
            m_Runtime.m_VehicleView.TryGetLine(right, out Entity rightLine);
            int lineOrder = CompareEntity(leftLine, rightLine);
            return lineOrder != 0 ? lineOrder : CompareEntity(left, right);
        }

        private void ResolveDirtyLines()
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!m_AllLinesDirty && (!m_Runtime.m_LineView.TryFrame(line, out LineFrame frame)
                        || !m_CurrentDirtyLineKeys.Contains(frame.StoreKey.ToString())))
                    {
                        continue;
                    }
                    m_ResolvedDirtyLines.Add(line);
                }
            }
            finally { lines.Dispose(); }
            m_ResolvedDirtyLines.Sort(CompareEntity);
        }
    }
}
