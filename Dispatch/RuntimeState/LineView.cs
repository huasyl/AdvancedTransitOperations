using System;
using System.Collections.Generic;
using System.Linq;
using RapidTransitMod.Dispatch;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LineView
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, bool> m_Exists;
        private readonly Func<uint> m_Frame;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<string, string> m_DraftKey;
        private readonly Func<string, LineKey> m_KeyById;
        private readonly Func<Entity, string, LineKey> m_KeyByLine;
        private readonly AppliedTimetableStore m_AppliedStore;
        private readonly Func<IReadOnlyDictionary<string, AppliedLine>> m_AppliedLines;
        private readonly LineConfig m_Cfg;
        private readonly Action m_DirtyTrack;
        private readonly Func<int, string> m_SlotText;
        private readonly Action<string> m_Log;
        private readonly Dictionary<Entity, LineFrame> m_Frames = new Dictionary<Entity, LineFrame>();
        private readonly Dictionary<Entity, ManagedLineFrame> m_ManagedFrames = new Dictionary<Entity, ManagedLineFrame>();
        private readonly Dictionary<Entity, bool> m_SupportCache = new Dictionary<Entity, bool>();
        private string m_LastLog = string.Empty;

        private readonly struct ManagedLineFrame
        {
            public readonly Entity Line;
            public readonly uint Frame;
            public readonly ulong AppliedVersion;
            public readonly bool Applied;

            public ManagedLineFrame(Entity line, uint frame, ulong appliedVersion, bool applied)
            {
                Line = line;
                Frame = frame;
                AppliedVersion = appliedVersion;
                Applied = applied;
            }
        }

        public LineView(
            EntityManager entityManager,
            Func<Entity, Entity> stop,
            Func<Entity, bool> exists,
            Func<uint> frame,
            Func<Entity, string> lineId,
            Func<string, string> draftKey,
            Func<string, LineKey> keyById,
            Func<Entity, string, LineKey> keyByLine,
            AppliedTimetableStore appliedStore,
            Func<IReadOnlyDictionary<string, AppliedLine>> appliedLines,
            LineConfig cfg,
            Action dirtyTrack,
            Func<int, string> slotText,
            Action<string> log)
        {
            m_EntityManager = entityManager;
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_Exists = exists ?? throw new ArgumentNullException(nameof(exists));
            m_Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_DraftKey = draftKey ?? throw new ArgumentNullException(nameof(draftKey));
            m_KeyById = keyById ?? throw new ArgumentNullException(nameof(keyById));
            m_KeyByLine = keyByLine ?? throw new ArgumentNullException(nameof(keyByLine));
            m_AppliedStore = appliedStore ?? throw new ArgumentNullException(nameof(appliedStore));
            m_AppliedLines = appliedLines ?? throw new ArgumentNullException(nameof(appliedLines));
            m_Cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            m_DirtyTrack = dirtyTrack ?? throw new ArgumentNullException(nameof(dirtyTrack));
            m_SlotText = slotText ?? throw new ArgumentNullException(nameof(slotText));
            m_Log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool TryFrame(Entity line, out LineFrame frame)
        {
            frame = default;
            if (line == Entity.Null || !m_Exists(line))
            {
                m_Frames.Remove(line);
                m_ManagedFrames.Remove(line);
                return false;
            }

            EnsureSupportCached(line);

            uint nowFrame = m_Frame();
            ulong cfgVersion = m_Cfg.Version;
            ulong appliedVersion = m_AppliedStore.Version;
            if (m_Frames.TryGetValue(line, out frame)
                && frame.Line == line
                && frame.Frame == nowFrame
                && frame.CfgVersion == cfgVersion
                && frame.AppliedVersion == appliedVersion)
            {
                return true;
            }

            string lineId = m_LineId(line);
            string lineKey = m_DraftKey(lineId);
            LineKey storeKey = m_KeyByLine(line, lineId);
            if (m_Frames.TryGetValue(line, out frame)
                && frame.Line == line
                && frame.CfgVersion == cfgVersion
                && frame.AppliedVersion == appliedVersion
                && string.Equals(frame.Id, lineId, StringComparison.Ordinal)
                && string.Equals(frame.Key, lineKey, StringComparison.Ordinal)
                && frame.StoreKey.Equals(storeKey))
            {
                return true;
            }

            IReadOnlyDictionary<string, AppliedLine> appliedLines = m_AppliedLines();
            appliedLines.TryGetValue(lineKey, out AppliedLine appliedState);
            bool storeManaged = false;
            string storeAppliedKind = string.Empty;
            bool hasStoreSummary = !storeKey.IsEmpty
                && m_AppliedStore.TryGetRuntimeSummary(storeKey, out storeManaged, out storeAppliedKind);
            bool applied = hasStoreSummary
                ? storeManaged
                : appliedLines.ContainsKey(lineKey);
            string cfgKind = Kind(lineId);
            string appliedKind = applied
                ? (hasStoreSummary ? storeAppliedKind : AppliedKind(appliedState))
                : string.Empty;
            string kind = !string.IsNullOrEmpty(cfgKind)
                ? cfgKind
                : appliedKind;

            bool supported = ComputeSupported(line);
            m_SupportCache[line] = supported;

            frame = new LineFrame(
                line,
                nowFrame,
                cfgVersion,
                appliedVersion,
                lineId,
                lineKey,
                storeKey,
                applied,
                cfgKind,
                appliedKind,
                kind);
            m_Frames[line] = frame;
            return true;
        }

        public LineInfo Get(Entity line)
        {
            if (line == Entity.Null || !m_Exists(line))
            {
                return new LineInfo(
                    line,
                    string.Empty,
                    string.Empty,
                    LineKey.Empty,
                    false,
                    Array.Empty<int>(),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes,
                    RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes,
                    string.Empty);
            }

            bool hasFrame = TryFrame(line, out LineFrame frame);
            string lineId = hasFrame ? frame.Id : m_LineId(line);
            string lineKey = hasFrame ? frame.Key : m_DraftKey(lineId);
            LineKey storeKey = m_KeyByLine(line, lineId);
            bool applied = hasFrame && frame.Applied;
            string cfgKind = hasFrame
                ? frame.CfgKind
                : Kind(lineId);
            string appliedKind = hasFrame ? frame.AppliedKind : string.Empty;
            string kind = hasFrame
                ? frame.Kind
                : (!string.IsNullOrEmpty(cfgKind) ? cfgKind : appliedKind);

            return new LineInfo(
                line,
                lineId,
                lineKey,
                storeKey,
                applied,
                applied ? Times(frame, storeKey) : Array.Empty<int>(),
                cfgKind,
                appliedKind,
                kind,
                Hold(lineId),
                Dwell(lineId),
                DepotId(lineId));
        }

        public int[] Times(Entity line)
        {
            return Get(line).Times;
        }

        public void Clear()
        {
            m_Frames.Clear();
            m_ManagedFrames.Clear();
            m_SupportCache.Clear();
            m_LastLog = string.Empty;
        }

        public void Dirty()
        {
            m_DirtyTrack();
        }

        private bool IsSupported(Entity line)
        {
            if (line == Entity.Null || !m_EntityManager.Exists(line))
                return false;

            if (m_SupportCache.TryGetValue(line, out bool supported))
                return supported;

            return false;
        }

        private bool EnsureSupportCached(Entity line)
        {
            if (line == Entity.Null || !m_EntityManager.Exists(line))
                return false;

            if (m_SupportCache.TryGetValue(line, out bool supported))
                return supported;

            supported = ComputeSupported(line);
            m_SupportCache[line] = supported;
            return supported;
        }

        private bool ComputeSupported(Entity line)
        {
            if (line == Entity.Null || !m_EntityManager.Exists(line))
                return false;

            LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(m_EntityManager, line, m_Stop);
            return support.Supported;
        }

        public bool Applied(Entity line)
        {
            return TryFrame(line, out LineFrame frame) && frame.Applied;
        }

        public bool Managed(Entity line, bool dispatchOn)
        {
            if (!dispatchOn)
                return false;
            if (!EnsureSupportCached(line))
                return false;
            return Applied(line);
        }

        public bool ManagedRuntime(Entity line, bool dispatchOn)
        {
            if (!dispatchOn)
                return false;

            if (!EnsureSupportCached(line))
                return false;

            if (line == Entity.Null || !m_Exists(line))
            {
                m_Frames.Remove(line);
                m_ManagedFrames.Remove(line);
                return false;
            }

            uint nowFrame = m_Frame();
            ulong appliedVersion = m_AppliedStore.Version;
            if (m_Frames.TryGetValue(line, out LineFrame frame)
                && frame.Line == line
                && frame.Frame == nowFrame
                && frame.AppliedVersion == appliedVersion)
            {
                return frame.Applied;
            }

            if (m_ManagedFrames.TryGetValue(line, out ManagedLineFrame managedFrame)
                && managedFrame.Line == line
                && managedFrame.Frame == nowFrame
                && managedFrame.AppliedVersion == appliedVersion)
            {
                return managedFrame.Applied;
            }

            LineKey storeKey = m_KeyByLine(line, null);
            bool storeManaged = false;
            bool hasStoreSummary = !storeKey.IsEmpty
                && m_AppliedStore.TryGetRuntimeSummary(storeKey, out storeManaged, out _);
            bool applied = hasStoreSummary && storeManaged;
            if (!hasStoreSummary)
            {
                string lineId = m_LineId(line);
                string lineKey = m_DraftKey(lineId);
                applied = m_AppliedLines().ContainsKey(lineKey);
            }

            m_ManagedFrames[line] = new ManagedLineFrame(line, nowFrame, appliedVersion, applied);
            return applied;
        }

        public bool TrySnapshot(Entity line, bool dispatchOn, out LineRuntimeSnapshot snapshot)
        {
            snapshot = default;
            if (!TryFrame(line, out LineFrame frame))
                return false;

            bool supported = IsSupported(line);
            bool managed = dispatchOn && supported && frame.Applied;
            bool local = supported && frame.Applied && string.Equals(frame.Kind, "local", StringComparison.Ordinal);
            bool express = supported && frame.Applied && string.Equals(frame.Kind, "express", StringComparison.Ordinal);
            snapshot = new LineRuntimeSnapshot(
                line,
                managed,
                local,
                express,
                0,
                frame);
            return true;
        }

        public void Log(Entity line, int nowMin, int nextSlot)
        {
            if (!RtLog.CacheInvalidationDiagnosticsEnabled)
                return;

            LineInfo info = Get(line);
            if (line == Entity.Null || !info.Applied)
                return;

            IReadOnlyDictionary<string, AppliedLine> appliedLines = m_AppliedLines();
            if (!appliedLines.TryGetValue(info.Key, out AppliedLine state))
                return;

            string staged = state.StagedRows != null && state.StagedRows.Count > 0
                ? string.Join(", ", state.StagedRows.Select(row =>
                    (row?.time ?? "-")
                    + "/"
                    + (string.IsNullOrEmpty(row?.kind) ? "-" : row.kind)
                    + "/"
                    + (string.IsNullOrEmpty(row?.source) ? "-" : row.source)))
                : "-";
            string cache = state.DepartureMinutesCache != null && state.DepartureMinutesCache.Length > 0
                ? string.Join(", ", state.DepartureMinutesCache.Select(m_SlotText))
                : "-";
            string key =
                info.Id
                + "|"
                + nowMin.ToString()
                + "|"
                + nextSlot.ToString()
                + "|"
                + staged
                + "|"
                + cache;
            if (string.Equals(key, m_LastLog, StringComparison.Ordinal))
                return;

            m_LastLog = key;
            m_Log(
                "[AppliedLineInspect] line="
                + info.Id
                + " now="
                + m_SlotText(nowMin)
                + " next="
                + (nextSlot >= 0 ? m_SlotText(nextSlot) : "-")
                + " cache=["
                + cache
                + "] staged=["
                + staged
                + "]");
        }

        public string AppliedKind(Entity line)
        {
            LineInfo info = Get(line);
            return info.Applied ? info.Kind : string.Empty;
        }

        public string AppliedKind(AppliedLine state)
        {
            if (state == null || state.StagedRows == null || state.StagedRows.Count == 0)
                return string.Empty;

            bool sawExpress = false;
            bool sawLocal = false;
            for (int i = 0; i < state.StagedRows.Count; i++)
            {
                string kind = state.StagedRows[i]?.kind;
                if (string.Equals(kind, "express", StringComparison.Ordinal))
                {
                    sawExpress = true;
                }
                else
                {
                    sawLocal = true;
                }

                if (sawExpress && sawLocal)
                    return "local";
            }

            if (sawExpress)
                return "express";

            return "local";
        }

        public string AppliedKind(LineKey lineKey, AppliedLine fallback)
        {
            if (!lineKey.IsEmpty
                && m_AppliedStore.TryGet(lineKey, out AppliedTimetableState state))
            {
                return state.ServiceKind ?? string.Empty;
            }

            return AppliedKind(fallback);
        }

        public string Kind(string lineId)
        {
            return m_Cfg.GetKind(lineId);
        }

        public string Kind(Entity line)
        {
            return Get(line).CfgKind;
        }

        public string Kind(string lineId, AppliedLine applied)
        {
            string cfgKind = Kind(lineId);
            if (!string.IsNullOrEmpty(cfgKind))
            {
                return cfgKind;
            }

            return AppliedKind(m_KeyById(lineId), applied);
        }

        public string Kind(Entity line, AppliedLine applied)
        {
            if (TryFrame(line, out LineFrame frame))
                return frame.Kind;

            if (line == Entity.Null || !m_Exists(line))
                return string.Empty;

            string lineId = m_LineId(line);
            string cfgKind = Kind(lineId);
            if (!string.IsNullOrEmpty(cfgKind))
            {
                return cfgKind;
            }

            return AppliedKind(m_KeyByLine(line, lineId), applied);
        }

        public bool Local(Entity line)
        {
            return EnsureSupportCached(line)
                && TryFrame(line, out LineFrame frame)
                && frame.Applied
                && string.Equals(frame.Kind, "local", StringComparison.Ordinal);
        }

        public bool Express(Entity line)
        {
            return EnsureSupportCached(line)
                && TryFrame(line, out LineFrame frame)
                && frame.Applied
                && string.Equals(frame.Kind, "express", StringComparison.Ordinal);
        }

        public int Hold(string lineId)
        {
            return m_Cfg.GetHold(lineId);
        }

        public int Hold(Entity line)
        {
            return m_Cfg.GetHold(line);
        }

        public int Dwell(string lineId)
        {
            return m_Cfg.GetDwell(lineId);
        }

        public int Dwell(Entity line)
        {
            return m_Cfg.GetDwell(line);
        }

        public string DepotId(string lineId)
        {
            return m_Cfg.GetDepotId(lineId);
        }

        public string DepotId(Entity line)
        {
            return m_Cfg.GetDepotId(line);
        }

        public ulong CfgVersion()
        {
            return m_Cfg.Version;
        }

        private int[] Times(LineFrame frame, LineKey storeKey)
        {
            if (!frame.Applied)
                return Array.Empty<int>();

            if (!storeKey.IsEmpty && m_AppliedStore.TryGet(storeKey, out AppliedTimetableState appliedState))
            {
                return appliedState.DepartureMinutes ?? Array.Empty<int>();
            }

            return Array.Empty<int>();
        }
    }
}
