using Game.Objects;
using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Core;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineTimes
    {
        private readonly LineTimesPort m_Port;
        private NativeHashMap<Entity, LineTimeProfileHeader> m_Profiles;
        private NativeList<float> m_RawSegmentFrames;
        private NativeList<float> m_SegmentFrames;
        private NativeList<float> m_StopFrames;
        private readonly List<FreeRange> m_FreeRanges = new List<FreeRange>();
        private readonly List<BusSegmentPlan> m_BusScratch = new List<BusSegmentPlan>(32);
        private readonly List<float> m_BusRawScratch = new List<float>(32);
        private readonly List<float> m_BusSegmentScratch = new List<float>(32);
        private readonly List<float> m_BusStopScratch = new List<float>(32);
        private readonly List<int> m_BusSourceSpanScratch = new List<int>(32);
        private bool m_BusBuildIssue;
        private readonly Dictionary<Entity, BusMissingKey> m_BusMissingSeg =
            new Dictionary<Entity, BusMissingKey>();

        private readonly struct FreeRange
        {
            internal readonly int Offset;
            internal readonly int Count;

            internal FreeRange(int offset, int count)
            {
                Offset = offset;
                Count = count;
            }
        }

        private readonly struct BusSegmentPlan
        {
            internal readonly int FromIndex;
            internal readonly Entity FromWaypoint;
            internal readonly Entity FromStop;
            internal readonly Entity ToWaypoint;
            internal readonly Entity ToStop;
            internal readonly int SegmentCount;
            internal readonly float EstimatedFrames;

            internal BusSegmentPlan(
                int fromIndex,
                Entity fromWaypoint,
                Entity fromStop,
                Entity toWaypoint,
                Entity toStop,
                int segmentCount,
                float estimatedFrames)
            {
                FromIndex = fromIndex;
                FromWaypoint = fromWaypoint;
                FromStop = fromStop;
                ToWaypoint = toWaypoint;
                ToStop = toStop;
                SegmentCount = segmentCount;
                EstimatedFrames = estimatedFrames;
            }
        }

        private readonly struct BusMissingKey
        {
            private readonly Entity m_Line;
            private readonly Entity m_FromWaypoint;
            private readonly Entity m_FromStop;
            private readonly Entity m_ToWaypoint;
            private readonly Entity m_ToStop;
            private readonly int m_Reason;

            internal BusMissingKey(
                Entity line,
                Entity fromWaypoint,
                Entity fromStop,
                Entity toWaypoint,
                Entity toStop,
                int reason)
            {
                m_Line = line;
                m_FromWaypoint = fromWaypoint;
                m_FromStop = fromStop;
                m_ToWaypoint = toWaypoint;
                m_ToStop = toStop;
                m_Reason = reason;
            }

            internal bool SameAs(BusMissingKey other)
            {
                return m_Line == other.m_Line
                    && m_FromWaypoint == other.m_FromWaypoint
                    && m_FromStop == other.m_FromStop
                    && m_ToWaypoint == other.m_ToWaypoint
                    && m_ToStop == other.m_ToStop
                    && m_Reason == other.m_Reason;
            }
        }

        public LineTimes(LineTimesPort port)
        {
            m_Port = port;
        }

        public void Init()
        {
            m_Profiles = new NativeHashMap<Entity, LineTimeProfileHeader>(64, Allocator.Persistent);
            m_RawSegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_SegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_StopFrames = new NativeList<float>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_Profiles.IsCreated) m_Profiles.Dispose();
            if (m_RawSegmentFrames.IsCreated) m_RawSegmentFrames.Dispose();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Dispose();
            if (m_StopFrames.IsCreated) m_StopFrames.Dispose();
        }

        public void Clear()
        {
            if (m_Profiles.IsCreated) m_Profiles.Clear();
            if (m_RawSegmentFrames.IsCreated) m_RawSegmentFrames.Clear();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Clear();
            if (m_StopFrames.IsCreated) m_StopFrames.Clear();
            m_FreeRanges.Clear();
            m_BusScratch.Clear();
            m_BusRawScratch.Clear();
            m_BusSegmentScratch.Clear();
            m_BusStopScratch.Clear();
            m_BusSourceSpanScratch.Clear();
            m_BusBuildIssue = false;
            m_BusMissingSeg.Clear();
        }

        public float Segment(LineTimeProfileHeader profile, int segmentIndex)
        {
            if (!m_SegmentFrames.IsCreated || segmentIndex < 0 || segmentIndex >= profile.m_Count)
                return 0f;

            return m_SegmentFrames[profile.m_Offset + segmentIndex];
        }

        public float StopValue(LineTimeProfileHeader profile, int stopIndex)
        {
            if (!m_StopFrames.IsCreated || stopIndex < 0 || stopIndex >= profile.m_Count)
                return 0f;

            return m_StopFrames[profile.m_Offset + stopIndex];
        }

        public ulong Signature(DynamicBuffer<RouteWaypoint> wps, DynamicBuffer<RouteSegment> segs)
        {
            ulong hash = 1469598103934665603UL;
            hash = m_Port.MixSignature(hash, wps.Length);
            hash = m_Port.MixSignature(hash, segs.Length);
            int count = math.min(wps.Length, segs.Length);
            for (int i = 0; i < count; i++)
            {
                hash = m_Port.MixSignature(hash, wps[i].m_Waypoint.Index);
                hash = m_Port.MixSignature(hash, segs[i].m_Segment.Index);
            }
            return hash;
        }

        public bool Get(Entity line, DynamicBuffer<RouteWaypoint> wps, out LineTimeProfileHeader profile)
        {
            profile = default;
            if (IsBus(line))
                return GetBus(line, wps, out profile);

            EntityManager entityManager = m_Port.EntityManager;
            if (!entityManager.HasBuffer<RouteSegment>(line)) return false;
            DynamicBuffer<RouteSegment> segs = entityManager.GetBuffer<RouteSegment>(line, true);
            if (wps.Length == 0 || segs.Length != wps.Length) return false;

            ulong signature = Signature(wps, segs);
            if (m_Profiles.TryGetValue(line, out var cached) && cached.m_Signature == signature)
            {
                profile = cached;
                return true;
            }
            if (m_Profiles.TryGetValue(line, out cached))
                InvalidateLine(line);

            if (!entityManager.HasComponent<PrefabRef>(line)) return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!entityManager.HasComponent<TransportLineData>(prefab)) return false;
            TransportLineData prefabLineData = entityManager.GetComponentData<TransportLineData>(prefab);

            int count = wps.Length;
            float baseLoopFrames = 0f;
            int offset = Allocate(count);

            for (int i = 0; i < count; i++)
            {
                Entity segment = segs[i].m_Segment;
                float segmentFrames = 0f;
                if (entityManager.HasComponent<PathInformation>(segment))
                {
                    PathInformation pathInfo = entityManager.GetComponentData<PathInformation>(segment);
                    segmentFrames = math.max(0f, pathInfo.m_Duration * 60f);
                }
                m_SegmentFrames[offset + i] = segmentFrames;
                m_StopFrames[offset + i] = Stop(line, wps, i, prefabLineData);
            }

            for (int i = 0; i < count; i++)
            {
                baseLoopFrames += m_SegmentFrames[offset + i];
                int nextWaypointIndex = (i + 1) % count;
                if (nextWaypointIndex != 0)
                    baseLoopFrames += m_StopFrames[offset + nextWaypointIndex];
            }

            if (baseLoopFrames <= 0f)
            {
                Release(offset, count);
                return false;
            }

            profile = new LineTimeProfileHeader
            {
                m_Signature = signature,
                m_BaseLoopFrames = baseLoopFrames,
                m_Offset = offset,
                m_Count = count
            };
            m_Profiles[line] = profile;
            return true;
        }

        public void RefreshBusSeg(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop)
        {
            if (!IsBus(line)
                || fromWaypoint == Entity.Null
                || fromStop == Entity.Null
                || toWaypoint == Entity.Null
                || toStop == Entity.Null
                || !TryCommitBusProfile(line, waypoints, true, out LineTimeProfileHeader profile))
            {
                return;
            }

            if (RtLog.VerboseEnabled)
            {
                m_Port.Log("[BusEta] phase=refresh line=" + line.Index
                    + " from=" + fromWaypoint.Index + "/" + fromStop.Index
                    + " to=" + toWaypoint.Index + "/" + toStop.Index
                    + " baseFrames=" + profile.m_BaseLoopFrames.ToString("F0"));
            }
        }

        public void InvalidateLine(Entity line)
        {
            m_BusMissingSeg.Remove(line);
            if (line == Entity.Null || !m_Profiles.TryGetValue(line, out LineTimeProfileHeader profile))
                return;

            m_Profiles.Remove(line);
            Release(profile.m_Offset, profile.m_Count);
        }

        private bool GetBus(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile)
        {
            profile = default;
            if (waypoints.Length < 2)
                return false;

            ulong signature = BusSignature(waypoints);
            if (m_Profiles.TryGetValue(line, out LineTimeProfileHeader cached)
                && cached.m_Signature == signature)
            {
                profile = cached;
                return true;
            }

            return TryCommitBusProfile(line, waypoints, true, out profile);
        }

        private bool TryCommitBusProfile(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool logIssues,
            out LineTimeProfileHeader profile)
        {
            profile = default;
            if (!BuildBusScratch(line, waypoints, logIssues, out float baseLoopFrames))
                return false;

            ulong signature = BusSignature(waypoints);
            bool hadProfile = m_Profiles.TryGetValue(line, out LineTimeProfileHeader previous);
            int offset;
            if (hadProfile && previous.m_Count == waypoints.Length)
            {
                offset = previous.m_Offset;
            }
            else
            {
                if (hadProfile)
                {
                    m_Profiles.Remove(line);
                    Release(previous.m_Offset, previous.m_Count);
                }
                offset = Allocate(waypoints.Length);
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                m_RawSegmentFrames[offset + i] = m_BusRawScratch[i];
                m_SegmentFrames[offset + i] = m_BusSegmentScratch[i];
                m_StopFrames[offset + i] = m_BusStopScratch[i];
            }

            profile = new LineTimeProfileHeader
            {
                m_Signature = signature,
                m_BaseLoopFrames = baseLoopFrames,
                m_Offset = offset,
                m_Count = waypoints.Length
            };
            m_Profiles[line] = profile;
            if (!m_BusBuildIssue)
                m_BusMissingSeg.Remove(line);
            if (RtLog.VerboseEnabled)
            {
                m_Port.Log("[BusEta] phase=profile line=" + line.Index
                    + " sources=" + m_BusScratch.Count
                    + " baseFrames=" + profile.m_BaseLoopFrames.ToString("F0"));
            }
            return true;
        }

        private bool BuildBusScratch(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool logIssues,
            out float baseLoopFrames)
        {
            baseLoopFrames = 0f;
            m_BusScratch.Clear();
            m_BusBuildIssue = false;
            if (waypoints.Length < 2)
                return false;

            EnsureBusScratch(waypoints.Length);
            Entity originWaypoint = waypoints[0].m_Waypoint;
            Entity originStop = ResolveStop(originWaypoint);
            if (originWaypoint == Entity.Null || originStop == Entity.Null)
            {
                if (logIssues)
                    LogBusIssue(line, originWaypoint, originStop, Entity.Null, Entity.Null, 1, "origin_stop_missing", 0f, 0f);
                return false;
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                if (!TryRawFrames(line, i, out float rawFrames))
                {
                    if (logIssues)
                    {
                        LogBusIssue(
                            line,
                            waypoints[i].m_Waypoint,
                            ResolveStop(waypoints[i].m_Waypoint),
                            Entity.Null,
                            Entity.Null,
                            2,
                            "path_weight_missing",
                            0f,
                            0f);
                    }
                    return false;
                }
                m_BusRawScratch[i] = rawFrames;
            }

            bool hasOriginSpan = false;
            for (int from = 0; from < waypoints.Length; from++)
            {
                Entity fromWaypoint = waypoints[from].m_Waypoint;
                Entity fromStop = ResolveStop(fromWaypoint);
                if (fromWaypoint == Entity.Null || fromStop == Entity.Null)
                    continue;

                for (int span = 1; span <= waypoints.Length; span++)
                {
                    int to = (from + span) % waypoints.Length;
                    Entity toWaypoint = waypoints[to].m_Waypoint;
                    Entity toStop = ResolveStop(toWaypoint);
                    if (toWaypoint == Entity.Null || toStop == Entity.Null)
                        continue;
                    if (!m_Port.TryBusSegFrames(
                            line,
                            fromWaypoint,
                            fromStop,
                            toWaypoint,
                            toStop,
                            out float estimatedFrames)
                        || !(estimatedFrames > 0f))
                    {
                        continue;
                    }

                    m_BusScratch.Add(new BusSegmentPlan(
                        from,
                        fromWaypoint,
                        fromStop,
                        toWaypoint,
                        toStop,
                        span,
                        estimatedFrames));
                    if (from == 0)
                        hasOriginSpan = true;
                }
            }

            if (!hasOriginSpan)
            {
                if (logIssues)
                    LogBusIssue(line, originWaypoint, originStop, Entity.Null, Entity.Null, 3, "origin_span_missing", 0f, 0f);
                return false;
            }

            SortBusPlans();
            for (int i = 0; i < m_BusScratch.Count; i++)
                ApplyBusPlan(line, waypoints.Length, m_BusScratch[i], logIssues);

            for (int i = 0; i < waypoints.Length; i++)
            {
                if (m_BusSourceSpanScratch[i] <= 0
                    || !(m_BusSegmentScratch[i] > 0f)
                    || !math.isfinite(m_BusSegmentScratch[i]))
                {
                    if (logIssues)
                    {
                        LogBusIssue(
                            line,
                            waypoints[i].m_Waypoint,
                            ResolveStop(waypoints[i].m_Waypoint),
                            Entity.Null,
                            Entity.Null,
                            4,
                            "route_coverage_missing",
                            0f,
                            0f);
                    }
                    return false;
                }

                m_BusStopScratch[i] = BusStop(line, i);
                baseLoopFrames += m_BusSegmentScratch[i];
                if (i > 0)
                    baseLoopFrames += m_BusStopScratch[i];
            }

            return math.isfinite(baseLoopFrames) && baseLoopFrames > 0f;
        }

        private bool ApplyBusPlan(
            Entity line,
            int count,
            BusSegmentPlan plan,
            bool logIssues)
        {
            float protectedFrames = 0f;
            float rawFrames = 0f;
            int unprotectedCount = 0;
            int cursor = plan.FromIndex;
            for (int part = 0; part < plan.SegmentCount; part++)
            {
                if (m_BusSourceSpanScratch[cursor] > 0)
                    protectedFrames += m_BusSegmentScratch[cursor];
                else
                {
                    rawFrames += m_BusRawScratch[cursor];
                    unprotectedCount++;
                }
                cursor = (cursor + 1) % count;
            }

            float remainingFrames = plan.EstimatedFrames - protectedFrames;
            if (unprotectedCount == 0)
            {
                float scale = math.max(1f, math.max(math.abs(plan.EstimatedFrames), math.abs(protectedFrames)));
                float tolerance = math.max(0.01f, scale * 16f * 1.1920929E-07f);
                if (math.abs(remainingFrames) <= tolerance)
                    return true;

                if (logIssues)
                    LogBusConflict(line, plan, "overlap_constraint_conflict", protectedFrames, remainingFrames);
                return false;
            }

            if (!(remainingFrames > 0f) || !math.isfinite(remainingFrames) || !(rawFrames > 0f))
            {
                if (logIssues)
                    LogBusConflict(line, plan, "overlap_remaining_non_positive", protectedFrames, remainingFrames);
                return false;
            }

            float allocatedFrames = 0f;
            int pending = unprotectedCount;
            cursor = plan.FromIndex;
            for (int part = 0; part < plan.SegmentCount; part++)
            {
                if (m_BusSourceSpanScratch[cursor] == 0)
                {
                    pending--;
                    float value = pending == 0
                        ? remainingFrames - allocatedFrames
                        : remainingFrames * m_BusRawScratch[cursor] / rawFrames;
                    if (!(value > 0f) || !math.isfinite(value))
                    {
                        if (logIssues)
                            LogBusConflict(line, plan, "overlap_projection_invalid", protectedFrames, remainingFrames);
                        return false;
                    }
                    allocatedFrames += value;
                }
                cursor = (cursor + 1) % count;
            }

            allocatedFrames = 0f;
            pending = unprotectedCount;
            cursor = plan.FromIndex;
            for (int part = 0; part < plan.SegmentCount; part++)
            {
                if (m_BusSourceSpanScratch[cursor] == 0)
                {
                    pending--;
                    float value = pending == 0
                        ? remainingFrames - allocatedFrames
                        : remainingFrames * m_BusRawScratch[cursor] / rawFrames;
                    m_BusSegmentScratch[cursor] = value;
                    m_BusSourceSpanScratch[cursor] = plan.SegmentCount;
                    allocatedFrames += value;
                }
                cursor = (cursor + 1) % count;
            }
            return true;
        }

        private void EnsureBusScratch(int count)
        {
            while (m_BusRawScratch.Count < count)
            {
                m_BusRawScratch.Add(0f);
                m_BusSegmentScratch.Add(0f);
                m_BusStopScratch.Add(0f);
                m_BusSourceSpanScratch.Add(0);
            }

            for (int i = 0; i < count; i++)
            {
                m_BusRawScratch[i] = 0f;
                m_BusSegmentScratch[i] = 0f;
                m_BusStopScratch[i] = 0f;
                m_BusSourceSpanScratch[i] = 0;
            }
        }

        private void SortBusPlans()
        {
            for (int i = 1; i < m_BusScratch.Count; i++)
            {
                BusSegmentPlan current = m_BusScratch[i];
                int insert = i - 1;
                while (insert >= 0 && CompareBusPlans(current, m_BusScratch[insert]) < 0)
                {
                    m_BusScratch[insert + 1] = m_BusScratch[insert];
                    insert--;
                }
                m_BusScratch[insert + 1] = current;
            }
        }

        private static int CompareBusPlans(BusSegmentPlan left, BusSegmentPlan right)
        {
            int span = left.SegmentCount.CompareTo(right.SegmentCount);
            return span != 0 ? span : left.FromIndex.CompareTo(right.FromIndex);
        }

        private void LogBusConflict(
            Entity line,
            BusSegmentPlan plan,
            string reason,
            float protectedFrames,
            float remainingFrames)
        {
            int reasonCode = reason == "overlap_constraint_conflict"
                ? 5
                : reason == "overlap_remaining_non_positive" ? 6 : 7;
            LogBusIssue(
                line,
                plan.FromWaypoint,
                plan.FromStop,
                plan.ToWaypoint,
                plan.ToStop,
                reasonCode,
                reason,
                protectedFrames,
                remainingFrames);
        }

        private void LogBusIssue(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop,
            int reasonCode,
            string reason,
            float protectedFrames,
            float remainingFrames)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (m_BusBuildIssue)
                return;
            m_BusBuildIssue = true;

            BusMissingKey current = new BusMissingKey(
                line,
                fromWaypoint,
                fromStop,
                toWaypoint,
                toStop,
                reasonCode);
            if (m_BusMissingSeg.TryGetValue(line, out BusMissingKey previous)
                && previous.SameAs(current))
            {
                return;
            }

            m_BusMissingSeg[line] = current;
            m_Port.Log("[BusEta] phase=reject line=" + line.Index
                + " from=" + fromWaypoint.Index + "/" + fromStop.Index
                + " to=" + toWaypoint.Index + "/" + toStop.Index
                + " reason=" + reason
                + " protected=" + protectedFrames.ToString("F0")
                + " remaining=" + remainingFrames.ToString("F0"));
        }

        private int Allocate(int count)
        {
            for (int i = 0; i < m_FreeRanges.Count; i++)
            {
                FreeRange range = m_FreeRanges[i];
                if (range.Count < count)
                    continue;

                int offset = range.Offset;
                if (range.Count == count)
                    m_FreeRanges.RemoveAt(i);
                else
                    m_FreeRanges[i] = new FreeRange(range.Offset + count, range.Count - count);
                ClearRange(offset, count);
                return offset;
            }

            int appendedOffset = m_SegmentFrames.Length;
            for (int i = 0; i < count; i++)
            {
                m_RawSegmentFrames.Add(0f);
                m_SegmentFrames.Add(0f);
                m_StopFrames.Add(0f);
            }
            return appendedOffset;
        }

        private void Release(int offset, int count)
        {
            if (offset < 0 || count <= 0)
                return;

            ClearRange(offset, count);
            int insert = 0;
            while (insert < m_FreeRanges.Count && m_FreeRanges[insert].Offset < offset)
                insert++;
            m_FreeRanges.Insert(insert, new FreeRange(offset, count));
            MergeFreeRanges();
        }

        private void ClearRange(int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                m_RawSegmentFrames[offset + i] = 0f;
                m_SegmentFrames[offset + i] = 0f;
                m_StopFrames[offset + i] = 0f;
            }
        }

        private void MergeFreeRanges()
        {
            for (int i = 1; i < m_FreeRanges.Count; i++)
            {
                FreeRange previous = m_FreeRanges[i - 1];
                FreeRange current = m_FreeRanges[i];
                if (previous.Offset + previous.Count < current.Offset)
                    continue;

                int end = math.max(previous.Offset + previous.Count, current.Offset + current.Count);
                m_FreeRanges[i - 1] = new FreeRange(previous.Offset, end - previous.Offset);
                m_FreeRanges.RemoveAt(i);
                i--;
            }
        }

        private bool IsBus(Entity line)
        {
            return line != Entity.Null && m_Port.ResolveMode(line) == TransitMode.Bus;
        }

        private ulong BusSignature(DynamicBuffer<RouteWaypoint> waypoints)
        {
            ulong hash = 1469598103934665603UL;
            hash = m_Port.MixSignature(hash, waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
                hash = m_Port.MixSignature(hash, waypoints[i].m_Waypoint.Index);
            return hash;
        }

        private Entity ResolveStop(Entity waypoint)
        {
            EntityManager entityManager = m_Port.EntityManager;
            if (waypoint == Entity.Null || !entityManager.Exists(waypoint))
                return Entity.Null;

            if (entityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = entityManager.GetComponentData<Connected>(waypoint).m_Connected;
                Entity resolved = ResolveOwnedStop(connected);
                if (resolved != Entity.Null)
                    return resolved;
            }

            return ResolveOwnedStop(waypoint);
        }

        private Entity ResolveOwnedStop(Entity entity)
        {
            EntityManager entityManager = m_Port.EntityManager;
            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null && entityManager.Exists(current); i++)
            {
                if (entityManager.HasComponent<Game.Routes.TransportStop>(current))
                    return current;
                if (!entityManager.HasComponent<Owner>(current))
                    break;
                current = entityManager.GetComponentData<Owner>(current).m_Owner;
            }
            return Entity.Null;
        }

        private bool TryRawFrames(Entity line, int index, out float frames)
        {
            frames = 0f;
            EntityManager entityManager = m_Port.EntityManager;
            if (!entityManager.HasBuffer<RouteSegment>(line))
                return false;

            DynamicBuffer<RouteSegment> segments = entityManager.GetBuffer<RouteSegment>(line, true);
            if (index < 0 || index >= segments.Length)
                return false;

            Entity segment = segments[index].m_Segment;
            if (segment == Entity.Null || !entityManager.HasComponent<PathInformation>(segment))
                return false;

            frames = entityManager.GetComponentData<PathInformation>(segment).m_Duration * 60f;
            return math.isfinite(frames) && frames > 0f;
        }

        public void RefreshObservedStop(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex)
        {
            if (line == Entity.Null
                || waypointIndex < 0
                || !m_Profiles.TryGetValue(line, out LineTimeProfileHeader profile)
                || waypointIndex >= profile.m_Count
                || !m_StopFrames.IsCreated
                || !m_Port.EntityManager.HasBuffer<RouteSegment>(line))
            {
                return;
            }

            bool isBus = IsBus(line);
            DynamicBuffer<RouteSegment> segments = m_Port.EntityManager.GetBuffer<RouteSegment>(line, true);
            ulong signature = isBus ? BusSignature(waypoints) : Signature(waypoints, segments);
            if (waypoints.Length != profile.m_Count
                || segments.Length != waypoints.Length
                || profile.m_Signature != signature)
            {
                return;
            }

            int stopOffset = profile.m_Offset + waypointIndex;
            if (stopOffset < 0 || stopOffset >= m_StopFrames.Length)
                return;

            float previousStopFrames = m_StopFrames[stopOffset];
            float updatedStopFrames;
            if (isBus)
            {
                updatedStopFrames = BusStop(line, waypointIndex);
            }
            else
            {
                if (!m_Port.EntityManager.HasComponent<PrefabRef>(line))
                    return;
                Entity prefab = m_Port.EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (!m_Port.EntityManager.HasComponent<TransportLineData>(prefab))
                    return;
                updatedStopFrames = Stop(
                    line,
                    waypoints,
                    waypointIndex,
                    m_Port.EntityManager.GetComponentData<TransportLineData>(prefab));
            }
            if (math.abs(previousStopFrames - updatedStopFrames) < 0.01f)
                return;

            m_StopFrames[stopOffset] = updatedStopFrames;
            if (waypointIndex != 0)
            {
                profile.m_BaseLoopFrames = math.max(
                    0f,
                    profile.m_BaseLoopFrames + updatedStopFrames - previousStopFrames);
                m_Profiles[line] = profile;
            }
        }

        private float BusStop(Entity line, int waypointIndex)
        {
            if (!m_Port.TryObservedWaypointStopFrames(line, waypointIndex, out float dwellFrames)
                || !(dwellFrames > 0f)
                || !math.isfinite(dwellFrames))
            {
                return 0f;
            }

            return math.max(0f, dwellFrames);
        }

        public float Stop(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            int waypointIndex,
            TransportLineData prefabLineData)
        {
            if (waypointIndex < 0 || waypointIndex >= wps.Length)
                return 0f;

            float configuredStopFrames = StopBase(wps[waypointIndex].m_Waypoint, prefabLineData);
            if (!(configuredStopFrames > 0f))
                return 0f;

            float dwellFrames = 0f;
            ClockSnapshot clockSnapshot = m_Port.ClockSnapshot();
            if (!m_Port.TryObservedWaypointStopFrames(line, waypointIndex, out dwellFrames))
            {
                int maxStationDwellMinutes = m_Port.DwellMinutes(line);
                if (maxStationDwellMinutes > 0)
                    dwellFrames = clockSnapshot.ToFramesCeil(maxStationDwellMinutes);
                else
                    dwellFrames = configuredStopFrames;
            }

            return math.max(
                0f,
                dwellFrames + clockSnapshot.ToFramesCeil(m_Port.ProfileStopStartBufferMinutes));
        }

        public float Depart(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int fromWaypointIndex, int targetWaypointIndex)
        {
            if (line == Entity.Null
                || waypoints.Length == 0
                || fromWaypointIndex < 0
                || fromWaypointIndex >= waypoints.Length
                || targetWaypointIndex < 0
                || targetWaypointIndex >= waypoints.Length
                || fromWaypointIndex == targetWaypointIndex)
            {
                return float.MaxValue;
            }

            if (Get(line, waypoints, out LineTimeProfileHeader profile))
                return Depart(profile, fromWaypointIndex, targetWaypointIndex);

            if (IsBus(line))
                return float.MaxValue;

            float lineDurationFrames = m_Port.ReadLapFrames(line);
            if (lineDurationFrames <= 0f)
                return float.MaxValue;

            int hopCount = (targetWaypointIndex - fromWaypointIndex + waypoints.Length) % waypoints.Length;
            if (hopCount <= 0)
                hopCount = waypoints.Length;

            return lineDurationFrames * (hopCount / (float)waypoints.Length);
        }

        public float Depart(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex)
        {
            int count = profile.m_Count;
            if (count == 0)
                return float.MaxValue;

            float remaining = 0f;
            int cursor = fromWaypointIndex;
            int guard = 0;

            while (cursor != targetWaypointIndex && guard++ < count)
            {
                remaining += m_SegmentFrames[profile.m_Offset + cursor];
                cursor = (cursor + 1) % count;
                if (cursor != targetWaypointIndex)
                    remaining += m_StopFrames[profile.m_Offset + cursor];
            }

            return guard <= count ? math.max(0f, remaining) : float.MaxValue;
        }

        public float ToWaypoint(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex)
        {
            if (Get(line, waypoints, out LineTimeProfileHeader profile))
            {
                float scale = Scale(vehicle, line, profile.m_BaseLoopFrames);
                if (m_Port.TryRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                {
                    float profiledFrames = ProfileTo(profile, nextWaypointIndex, segmentPosition, targetWaypointIndex);
                    if (profiledFrames != float.MaxValue)
                        return profiledFrames * scale;
                }

                int cachedWaypointIndex = m_Port.CachedWaypointIndex(vehicle);
                float cachedFrames = Depart(profile, cachedWaypointIndex, targetWaypointIndex);
                if (cachedFrames != float.MaxValue)
                    return cachedFrames * scale;
            }

            return float.MaxValue;
        }

        public float Prep(
            Entity v,
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            float lineDurationFrames)
        {
            if (!m_Port.IsPreparingKnown(v))
                return float.MaxValue;
            float cachedFrames = m_Port.ReadDispatchFrames(line);
            if (cachedFrames <= 0f)
                cachedFrames = DispatchFallback(v, line, lineDurationFrames);
            if (cachedFrames <= 0f)
                return float.MaxValue;
            cachedFrames = math.clamp(
                cachedFrames,
                m_Port.DispatchEstimateMinFrames,
                m_Port.DispatchEstimateMaxFrames);

            if (wps.Length > 0
                && m_Port.EntityManager.HasComponent<Target>(v)
                && m_Port.EntityManager.GetComponentData<Target>(v).m_Target == wps[0].m_Waypoint
                && m_Port.TryRouteProgress(v, out int nextWaypointIndex, out float segmentPosition)
                && nextWaypointIndex == 0)
            {
                return math.max(0f, cachedFrames * (1f - math.saturate(segmentPosition)));
            }

            return cachedFrames;
        }

        public float Run(
            Entity v,
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            uint nowFrame,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            if (Get(line, wps, out var profile))
            {
                float scale = Scale(v, line, profile.m_BaseLoopFrames);

                if (m_Port.TryRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                    return Remain(profile, nextWaypointIndex, segmentPosition) * scale;

                int cachedWpIdx = m_Port.CachedWaypointIndex(v);
                float cachedWaypointEstimate = CachedRemain(profile, cachedWpIdx);
                if (cachedWaypointEstimate != float.MaxValue)
                    return cachedWaypointEstimate * scale;
            }

            if (IsBus(line))
            {
                // 公交站间观测不完整时仅Running路线ETA未知；Preparing仍走现有Prep，Scheduler保持既有规则，禁止用raw或Lap补齐。
                return float.MaxValue;
            }

            float lapFrames = m_Port.TryLapFrames(v, out uint vehicleLapFrames) && vehicleLapFrames > 0
                ? vehicleLapFrames
                : 0f;
            if (lapFrames <= 0f && lineHasHistory && lineDurationFrames > 0f)
                lapFrames = lineDurationFrames;

            if (lapFrames <= 0f)
                return float.MaxValue;

            if (m_Port.TryLapStartFrame(v, out uint lapStartFrame))
                return math.max(0f, lapFrames - (float)(nowFrame - lapStartFrame));

            return float.MaxValue;
        }

        public float RunTo(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex)
        {
            return ToWaypoint(vehicle, line, waypoints, targetWaypointIndex);
        }

        public float ProfileTo(
            LineTimeProfileHeader profile,
            int nextWaypointIndex,
            float segmentPosition,
            int targetWaypointIndex)
        {
            int count = profile.m_Count;
            if (count == 0
                || nextWaypointIndex < 0
                || nextWaypointIndex >= count
                || targetWaypointIndex < 0
                || targetWaypointIndex >= count)
            {
                return float.MaxValue;
            }

            int segmentIndex = nextWaypointIndex == 0 ? count - 1 : nextWaypointIndex - 1;
            float remaining = m_SegmentFrames[profile.m_Offset + segmentIndex] * (1f - math.saturate(segmentPosition));
            int cursor = nextWaypointIndex;
            int guard = 0;

            while (cursor != targetWaypointIndex && guard++ < count)
            {
                remaining += m_StopFrames[profile.m_Offset + cursor];
                remaining += m_SegmentFrames[profile.m_Offset + cursor];
                cursor = (cursor + 1) % count;
            }

            return guard <= count ? math.max(0f, remaining) : float.MaxValue;
        }

        public float Scale(Entity v, Entity line, float baseLoopFrames)
        {
            if (IsBus(line))
                return 1f;
            if (baseLoopFrames <= 0f) return 1f;
            if (!m_Port.TryLapFrames(v, out uint observedLoopFrames) || observedLoopFrames == 0)
                return 1f;

            float rawScale = observedLoopFrames / baseLoopFrames;
            if (rawScale < m_Port.EtaScaleMin || rawScale > m_Port.EtaScaleMax)
                return 1f;
            return rawScale;
        }

        public float Remain(LineTimeProfileHeader profile, int nextWaypointIndex, float segmentPosition)
        {
            int count = profile.m_Count;
            if (count == 0) return float.MaxValue;
            if (nextWaypointIndex < 0 || nextWaypointIndex >= count) return float.MaxValue;

            int segmentIndex = nextWaypointIndex == 0 ? count - 1 : nextWaypointIndex - 1;
            float remaining = m_SegmentFrames[profile.m_Offset + segmentIndex] * (1f - math.saturate(segmentPosition));

            for (int waypointIndex = nextWaypointIndex; waypointIndex != 0; waypointIndex = (waypointIndex + 1) % count)
            {
                remaining += m_StopFrames[profile.m_Offset + waypointIndex];
                remaining += m_SegmentFrames[profile.m_Offset + waypointIndex];
            }
            return math.max(0f, remaining);
        }

        public float CachedRemain(LineTimeProfileHeader profile, int cachedWpIdx)
        {
            int count = profile.m_Count;
            if (count == 0) return float.MaxValue;
            if (cachedWpIdx < 0 || cachedWpIdx >= count) return float.MaxValue;

            float remaining = m_SegmentFrames[profile.m_Offset + cachedWpIdx];
            int nextWaypointIndex = (cachedWpIdx + 1) % count;
            for (int waypointIndex = nextWaypointIndex; waypointIndex != 0; waypointIndex = (waypointIndex + 1) % count)
            {
                remaining += m_StopFrames[profile.m_Offset + waypointIndex];
                remaining += m_SegmentFrames[profile.m_Offset + waypointIndex];
            }
            return math.max(0f, remaining);
        }

        public float StopBase(Entity waypoint, TransportLineData prefabLineData)
        {
            EntityManager entityManager = m_Port.EntityManager;
            if (waypoint == Entity.Null
                || !entityManager.Exists(waypoint)
                || !entityManager.HasComponent<VehicleTiming>(waypoint))
                return 0f;

            float stopDuration = prefabLineData.m_StopDuration;
            if (entityManager.HasComponent<Connected>(waypoint))
            {
                Entity connectedStop = entityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connectedStop != Entity.Null
                    && entityManager.Exists(connectedStop)
                    && entityManager.HasComponent<Game.Routes.TransportStop>(connectedStop))
                    stopDuration = RouteUtils.GetStopDuration(prefabLineData, entityManager.GetComponentData<Game.Routes.TransportStop>(connectedStop));
            }
            return math.max(0f, stopDuration * 60f);
        }

        public float DispatchFallback(Entity v, Entity line, float lineDurationFrames)
        {
            float estimateFrames = 0f;
            EntityManager entityManager = m_Port.EntityManager;
            if (entityManager.HasComponent<Transform>(v))
            {
                float3 vehiclePos = entityManager.GetComponentData<Transform>(v).m_Position;
                Entity stopA = FirstStop(line);
                if (stopA != Entity.Null && entityManager.HasComponent<Transform>(stopA))
                {
                    float3 stopPos = entityManager.GetComponentData<Transform>(stopA).m_Position;
                    float distanceMeters = math.distance(vehiclePos, stopPos);
                    estimateFrames = distanceMeters * m_Port.DispatchFallbackFramesPerMeter;
                }
            }
            if (estimateFrames <= 0f && lineDurationFrames > 0f)
                estimateFrames = lineDurationFrames * 0.2f;
            if (estimateFrames <= 0f)
                estimateFrames = m_Port.DispatchEstimateDefaultFrames;
            return math.clamp(
                estimateFrames,
                m_Port.DispatchEstimateMinFrames,
                m_Port.DispatchEstimateMaxFrames);
        }

        public Entity FirstStop(Entity line)
        {
            EntityManager entityManager = m_Port.EntityManager;
            if (!entityManager.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;

            DynamicBuffer<RouteWaypoint> wps = entityManager.GetBuffer<RouteWaypoint>(line, true);
            if (wps.Length == 0)
                return Entity.Null;
            Entity stationA = wps[0].m_Waypoint;
            if (stationA == Entity.Null
                || !entityManager.Exists(stationA)
                || !entityManager.HasComponent<Connected>(stationA))
                return Entity.Null;
            Entity connected = entityManager.GetComponentData<Connected>(stationA).m_Connected;
            return connected != Entity.Null && entityManager.Exists(connected)
                ? connected
                : Entity.Null;
        }

        public float Duration(Entity line)
        {
            EntityManager entityManager = m_Port.EntityManager;
            if (IsBus(line))
            {
                if (!entityManager.HasBuffer<RouteWaypoint>(line))
                    return 0f;

                DynamicBuffer<RouteWaypoint> busWaypoints = entityManager.GetBuffer<RouteWaypoint>(line, true);
                return Get(line, busWaypoints, out LineTimeProfileHeader busProfile)
                    ? busProfile.m_BaseLoopFrames / 60f
                    : 0f;
            }

            if (!entityManager.HasComponent<PrefabRef>(line)) return 0f;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!entityManager.HasComponent<TransportLineData>(prefab)) return 0f;
            float stopDuration = entityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration;

            if (!entityManager.HasBuffer<RouteWaypoint>(line)) return 0f;
            if (!entityManager.HasBuffer<RouteSegment>(line)) return 0f;
            DynamicBuffer<RouteWaypoint> waypoints = entityManager.GetBuffer<RouteWaypoint>(line, true);
            DynamicBuffer<RouteSegment> segments = entityManager.GetBuffer<RouteSegment>(line, true);
            if (waypoints.Length == 0 || segments.Length == 0) return 0f;

            int firstWaypoint = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (entityManager.HasComponent<VehicleTiming>(waypoints[w].m_Waypoint))
                {
                    firstWaypoint = w;
                    break;
                }
            }

            float pathDuration = 0f;
            for (int i = 0; i < waypoints.Length; i++)
            {
                int wi = (firstWaypoint + i) % waypoints.Length;
                int wi1 = (wi + 1) % waypoints.Length;
                Entity segment = segments[wi].m_Segment;
                if (entityManager.HasComponent<PathInformation>(segment))
                    pathDuration += entityManager.GetComponentData<PathInformation>(segment).m_Duration;
                if (entityManager.HasComponent<VehicleTiming>(waypoints[wi1].m_Waypoint))
                    pathDuration += stopDuration;
            }

            return pathDuration;
        }
    }
}
