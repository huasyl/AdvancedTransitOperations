using Game.Simulation;
using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Core;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Curve = Game.Net.Curve;

namespace RapidTransitMod.Dispatch.Lines
{
    internal readonly struct LineTimesChangeResult
    {
        internal readonly LineTimesPlanKind Kind;
        internal readonly bool Applied;

        internal LineTimesChangeResult(LineTimesPlanKind kind, bool applied)
        {
            Kind = kind;
            Applied = applied;
        }
    }

    internal enum ArrivalKind : byte { Unknown, InStation, Estimated }
    internal enum LeadSource : byte { Theory, History, Lap, Default, BusHistory, BusNone, Pending }
    internal enum EtaSource : byte { None, Path, Monitor, Bus }
    internal enum ArrivalMethod : byte
    {
        None, InStation, Outbound, OutboundExpired, Stop, Progress, CachedWaypoint, Lap, OutboundPending
    }

    internal readonly struct ArrivalBasis
    {
        internal readonly ArrivalMethod Method;
        internal readonly EtaSource Source;
        internal readonly int Waypoint;
        internal readonly float Position;
        internal readonly float Scale;

        internal ArrivalBasis(ArrivalMethod method, EtaSource source = EtaSource.None,
            int waypoint = -1, float position = -1f, float scale = 1f)
        {
            Method = method;
            Source = source;
            Waypoint = waypoint;
            Position = position;
            Scale = scale;
        }
    }

    internal sealed class LineTimes
    {
        private readonly LineTimesPort m_Port;
        private NativeHashMap<Entity, LineTimeProfileHeader> m_Profiles;
        private NativeList<float> m_RawSegmentFrames;
        private NativeList<float> m_SegmentFrames;
        private NativeList<float> m_StopFrames;
        private NativeList<float> m_EtaSeg;
        private NativeList<float> m_EtaDwell;
        private NativeList<float> m_EtaLeft;
        private readonly SimulationSystem m_Simulation;
        private readonly VehicleView m_Vehicles;
        private readonly StopRuntime m_Stops;
        private readonly MonitorAverageStore m_Monitor;
        private readonly DispatchCache m_Dispatch;
        private readonly SpawnLeadTheory m_Theory;
        private readonly LineStructureInvalidator m_Invalidator;
        private readonly Dictionary<Entity, LineStopLayout> m_Layouts = new Dictionary<Entity, LineStopLayout>();
        private readonly Dictionary<Entity, float> m_LineMeters = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, bool> m_BusLines = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, bool> m_PendingLeadLines = new Dictionary<Entity, bool>();
        private readonly HashSet<Entity> m_Tried = new HashSet<Entity>();
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

        public LineTimes(LineTimesPort port, SimulationSystem simulation, VehicleView vehicles,
            StopRuntime stops, MonitorAverageStore monitor, DispatchCache dispatch,
            SpawnLeadTheory theory, LineStructureInvalidator invalidator)
        {
            m_Port = port;
            m_Simulation = simulation;
            m_Vehicles = vehicles;
            m_Stops = stops;
            m_Monitor = monitor;
            m_Dispatch = dispatch;
            m_Theory = theory;
            m_Invalidator = invalidator;
        }

        public void Init()
        {
            m_Profiles = new NativeHashMap<Entity, LineTimeProfileHeader>(64, Allocator.Persistent);
            m_RawSegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_SegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_StopFrames = new NativeList<float>(256, Allocator.Persistent);
            m_EtaSeg = new NativeList<float>(256, Allocator.Persistent);
            m_EtaDwell = new NativeList<float>(256, Allocator.Persistent);
            m_EtaLeft = new NativeList<float>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_Profiles.IsCreated) m_Profiles.Dispose();
            if (m_RawSegmentFrames.IsCreated) m_RawSegmentFrames.Dispose();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Dispose();
            if (m_StopFrames.IsCreated) m_StopFrames.Dispose();
            if (m_EtaSeg.IsCreated) m_EtaSeg.Dispose();
            if (m_EtaDwell.IsCreated) m_EtaDwell.Dispose();
            if (m_EtaLeft.IsCreated) m_EtaLeft.Dispose();
        }

        public void Clear()
        {
            if (m_Profiles.IsCreated) m_Profiles.Clear();
            if (m_RawSegmentFrames.IsCreated) m_RawSegmentFrames.Clear();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Clear();
            if (m_StopFrames.IsCreated) m_StopFrames.Clear();
            if (m_EtaSeg.IsCreated) m_EtaSeg.Clear();
            if (m_EtaDwell.IsCreated) m_EtaDwell.Clear();
            if (m_EtaLeft.IsCreated) m_EtaLeft.Clear();
            m_FreeRanges.Clear();
            m_BusScratch.Clear();
            m_BusRawScratch.Clear();
            m_BusSegmentScratch.Clear();
            m_BusStopScratch.Clear();
            m_BusSourceSpanScratch.Clear();
            m_BusBuildIssue = false;
            m_BusMissingSeg.Clear();
            m_Layouts.Clear();
            m_LineMeters.Clear();
            m_BusLines.Clear();
            m_PendingLeadLines.Clear();
            m_Tried.Clear();
        }

        public ArrivalKind Arrival(Entity vehicle, Entity line, out uint frame)
            => Arrival(vehicle, line, out frame, out _);

        public ArrivalKind Arrival(Entity vehicle, Entity line, out uint frame, out ArrivalBasis basis)
        {
            frame = 0u;
            if (m_Stops.TryGetSession(vehicle, out Entity sessionLine, out int wp, out uint arrival)
                && sessionLine == line && wp == 0)
            {
                frame = arrival;
                basis = new ArrivalBasis(ArrivalMethod.InStation);
                return ArrivalKind.InStation;
            }

            if (m_Vehicles.TryGetTheoryWait(vehicle, out _))
            {
                basis = new ArrivalBasis(ArrivalMethod.OutboundPending);
                return ArrivalKind.Unknown;
            }

            uint now = m_Simulation.frameIndex;
            bool hasEstimatedOriginArrival =
                m_Vehicles.TryGetEstimatedOriginArrivalFrame(vehicle, out uint estimatedOriginArrivalFrame);
            if (hasEstimatedOriginArrival && unchecked((int)(now - estimatedOriginArrivalFrame)) > 0)
            {
                basis = new ArrivalBasis(ArrivalMethod.OutboundExpired);
                return ArrivalKind.Unknown;
            }
            if (hasEstimatedOriginArrival)
            {
                frame = estimatedOriginArrivalFrame;
                basis = new ArrivalBasis(ArrivalMethod.Outbound);
                return ArrivalKind.Estimated;
            }

            if (TryEtaProfile(line, out LineTimeProfileHeader profile))
            {
                float scale = EtaScale(vehicle, line, profile);
                float remaining;
                if (sessionLine == line && wp > 0 && wp < profile.m_Count)
                {
                    remaining = math.max(0f, scale * m_EtaDwell[profile.m_Offset + wp] - unchecked((int)(now - arrival)))
                        + scale * m_EtaLeft[profile.m_Offset + wp];
                    basis = new ArrivalBasis(ArrivalMethod.Stop, (EtaSource)profile.m_EtaSource, wp, -1f, scale);
                }
                else if (m_Port.TryRouteProgress(vehicle, out int next, out float position)
                    && next >= 0 && next < profile.m_Count)
                {
                    int segment = next == 0 ? profile.m_Count - 1 : next - 1;
                    remaining = m_EtaSeg[profile.m_Offset + segment] * (1f - math.saturate(position));
                    if (next != 0)
                        remaining += m_EtaDwell[profile.m_Offset + next] + m_EtaLeft[profile.m_Offset + next];
                    remaining *= scale;
                    basis = new ArrivalBasis(ArrivalMethod.Progress, (EtaSource)profile.m_EtaSource, next, position, scale);
                }
                else
                {
                    int cached = m_Port.CachedWaypointIndex(vehicle);
                    if (cached < 0 || cached >= profile.m_Count)
                        return LapArrival(vehicle, line, now, out frame, out basis);
                    remaining = scale * m_EtaLeft[profile.m_Offset + cached];
                    basis = new ArrivalBasis(ArrivalMethod.CachedWaypoint, (EtaSource)profile.m_EtaSource, cached, -1f, scale);
                }
                frame = AddFrames(now, remaining);
                return ArrivalKind.Estimated;
            }
            return LapArrival(vehicle, line, now, out frame, out basis);
        }

        private ArrivalKind LapArrival(Entity vehicle, Entity line, uint now, out uint frame, out ArrivalBasis basis)
        {
            frame = 0u;
            basis = new ArrivalBasis(ArrivalMethod.None);
            if (IsBus(line)) return ArrivalKind.Unknown;
            float lap = m_Port.TryLapFrames(vehicle, out uint observed) && observed > 0u
                ? observed : m_Port.ReadLapFrames(line);
            if (!(lap > 0f) || !m_Port.TryLapStartFrame(vehicle, out uint start))
                return ArrivalKind.Unknown;
            frame = AddFrames(now, math.max(0f, lap - unchecked((int)(now - start))));
            basis = new ArrivalBasis(ArrivalMethod.Lap);
            return ArrivalKind.Estimated;
        }

        private bool TryEtaProfile(Entity line, out LineTimeProfileHeader profile)
        {
            profile = default;
            if (m_Port.IsLinePending(line)) return false;
            if (m_Profiles.TryGetValue(line, out profile))
                return profile.m_Count > 0 && profile.m_EtaSource != (byte)EtaSource.None;
            if (!m_Tried.Add(line) || !m_Port.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;
            return Get(line, m_Port.EntityManager.GetBuffer<RouteWaypoint>(line, true), out profile)
                && profile.m_EtaSource != (byte)EtaSource.None;
        }

        private float EtaScale(Entity vehicle, Entity line, LineTimeProfileHeader profile)
        {
            if (profile.m_EtaSource != (byte)EtaSource.Path) return 1f;
            float total = m_EtaLeft[profile.m_Offset];
            if (!(total > 0f)) return 1f;
            float lap = m_Port.TryLapFrames(vehicle, out uint observed) && observed > 0u
                ? observed : m_Port.ReadLapFrames(line);
            float scale = lap / total;
            return scale >= m_Port.EtaScaleMin && scale <= m_Port.EtaScaleMax ? scale : 1f;
        }

        private static uint AddFrames(uint frame, float frames)
            => unchecked(frame + (uint)math.ceil(math.max(0f, frames)));

        public float Lead(Entity line) => Lead(line, out _);

        public float Lead(Entity line, out LeadSource source)
            => ReadLead(line, out source, ensureTheory: true);

        private float ReadLead(Entity line, out LeadSource source, bool ensureTheory)
        {
            float frames;
            if (IsBus(line))
            {
                frames = m_Dispatch.Read(line);
                source = frames > 0f ? LeadSource.BusHistory : LeadSource.BusNone;
                return frames > 0f ? math.clamp(frames, m_Port.DispatchEstimateMinFrames,
                    m_Port.ClockSnapshot().ToFramesCeil(60d)) : 0f;
            }
            if (ensureTheory && m_Port.EntityManager.HasBuffer<RouteWaypoint>(line))
                m_Theory.Ensure(line, m_Port.EntityManager.GetBuffer<RouteWaypoint>(line, true));
            if (m_Theory.TryRead(line, out frames)) source = LeadSource.Theory;
            else if ((frames = m_Dispatch.Read(line)) > 0f) source = LeadSource.History;
            else if ((frames = m_Port.ReadLapFrames(line) * 0.2f) > 0f) source = LeadSource.Lap;
            else { frames = m_Port.DispatchEstimateDefaultFrames; source = LeadSource.Default; }
            if (ensureTheory && source != LeadSource.Theory && m_Theory.IsPending(line))
            {
                source = LeadSource.Pending;
                if (!m_PendingLeadLines.ContainsKey(line))
                    m_PendingLeadLines.Add(line, false);
            }
            return math.max(m_Port.DispatchEstimateMinFrames, frames);
        }

        internal bool TryMarkSpawnDeferred(Entity line)
        {
            if (m_PendingLeadLines[line]) return false;
            m_PendingLeadLines[line] = true;
            return true;
        }

        internal List<Entity> TakeSettledLeadLines()
        {
            if (m_PendingLeadLines.Count == 0) return null;

            List<Entity> settled = null;
            foreach (Entity line in m_PendingLeadLines.Keys)
            {
                if (!m_Theory.IsPending(line))
                    (settled ??= new List<Entity>()).Add(line);
            }
            if (settled != null)
                foreach (Entity line in settled)
                    m_PendingLeadLines.Remove(line);
            return settled;
        }

        public bool TryEstimateNewVehicleOriginArrivalFrame(
            Entity vehicle, Entity line, uint? spawnOrderRequestFrame, uint registerFrame,
            out uint result, uint? settleFrame = null)
        {
            float lead = ReadLead(line, out LeadSource source, ensureTheory: !settleFrame.HasValue);
            if (source == LeadSource.Pending)
            {
                if (RtLog.VerboseEnabled)
                    m_Port.Log("[OutboundEta] line=" + line.Index + " vehicle=" + vehicle.Index + " method=pending"
                        + " request=" + (spawnOrderRequestFrame.HasValue ? spawnOrderRequestFrame.Value.ToString() : "-")
                        + " register=" + registerFrame + " lead=" + lead.ToString("F0") + "(" + source + ")");
                result = 0u;
                return false;
            }
            float prep = source == LeadSource.Theory || source == LeadSource.History || source == LeadSource.BusHistory
                ? m_Dispatch.ReadPrep(line) : 0f;
            float travel = math.max(0f, lead - prep);
            string method = "order";
            string bound = null;
            string fallback = null;
            float pathLeft = -1f;
            float remainingMeters = 0f;
            float totalMeters = 0f;
            uint baseFrame = spawnOrderRequestFrame.HasValue ? registerFrame : settleFrame ?? registerFrame;
            if (spawnOrderRequestFrame.HasValue)
            {
                uint requested = AddFrames(spawnOrderRequestFrame.Value, lead);
                uint registered = AddFrames(registerFrame, travel);
                bool byRequest = unchecked((int)(requested - registered)) >= 0;
                result = byRequest ? requested : registered;
                bound = byRequest ? "request" : "register";
            }
            else
            {
                method = "lead";
                result = AddFrames(baseFrame, lead);
                fallback = ReadPathMeters(vehicle, line, out remainingMeters, out totalMeters);
                if (fallback == null)
                {
                    method = "path";
                    pathLeft = math.saturate(remainingMeters / totalMeters);
                    result = AddFrames(baseFrame, travel * pathLeft);
                }
            }
            if (RtLog.VerboseEnabled)
            {
                m_Port.Log("[OutboundEta] line=" + line.Index + " vehicle=" + vehicle.Index
                    + " method=" + method + (bound != null ? " bound=" + bound : string.Empty)
                    + (settleFrame.HasValue ? " stage=settle" : string.Empty)
                    + " request=" + (spawnOrderRequestFrame.HasValue ? spawnOrderRequestFrame.Value.ToString() : "-")
                    + " register=" + registerFrame + " lead=" + lead.ToString("F0") + "(" + source + ")"
                    + " prep=" + prep.ToString("F0") + " travel=" + travel.ToString("F0")
                    + " pathLeft=" + (pathLeft >= 0f ? pathLeft.ToString("F2") : "-")
                    + (pathLeft >= 0f ? " meters=" + remainingMeters.ToString("F1") + "/" + totalMeters.ToString("F1") : string.Empty)
                    + (fallback != null ? " fallback=" + fallback : string.Empty)
                    + " result=" + result
                    + " resultIn=" + m_Port.ClockSnapshot().ToMinutes(unchecked((int)(result - baseFrame))).ToString("F1") + "min");
            }
            return true;
        }

        private string ReadPathMeters(Entity vehicle, Entity line, out float remaining, out float total)
        {
            EntityManager em = m_Port.EntityManager;
            remaining = 0f;
            PathInformation info = em.HasComponent<PathInformation>(vehicle)
                ? em.GetComponentData<PathInformation>(vehicle) : default;
            total = info.m_Distance;
            if (total <= 0f) return "distance";
            if (!em.HasComponent<PathOwner>(vehicle)) return "path-state";
            PathOwner owner = em.GetComponentData<PathOwner>(vehicle);
            if ((owner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) != 0)
                return "path-state";
            Entity target = em.HasComponent<Target>(vehicle) ? em.GetComponentData<Target>(vehicle).m_Target : Entity.Null;
            if (info.m_Destination != target) return "destination";
            if (!em.HasBuffer<RouteWaypoint>(line)) return "target";
            DynamicBuffer<RouteWaypoint> waypoints = em.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || target != waypoints[0].m_Waypoint) return "target";

            if (IsBus(line))
            {
                if (em.HasComponent<CarCurrentLane>(vehicle))
                {
                    CarCurrentLane lane = em.GetComponentData<CarCurrentLane>(vehicle);
                    remaining += LaneMeters(em, lane.m_Lane, lane.m_CurvePosition.z - lane.m_CurvePosition.x);
                }
                if (em.HasBuffer<CarNavigationLane>(vehicle))
                {
                    DynamicBuffer<CarNavigationLane> lanes = em.GetBuffer<CarNavigationLane>(vehicle, true);
                    foreach (CarNavigationLane lane in lanes)
                        remaining += LaneMeters(em, lane.m_Lane, lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                }
            }
            else
            {
                if (em.HasBuffer<LayoutElement>(vehicle))
                {
                    DynamicBuffer<LayoutElement> layout = em.GetBuffer<LayoutElement>(vehicle, true);
                    if (layout.Length > 0 && em.HasComponent<TrainCurrentLane>(layout[0].m_Vehicle))
                    {
                        TrainBogieLane front = em.GetComponentData<TrainCurrentLane>(layout[0].m_Vehicle).m_Front;
                        remaining += LaneMeters(em, front.m_Lane, front.m_CurvePosition.w - front.m_CurvePosition.y);
                    }
                }
                if (em.HasBuffer<TrainNavigationLane>(vehicle))
                {
                    DynamicBuffer<TrainNavigationLane> lanes = em.GetBuffer<TrainNavigationLane>(vehicle, true);
                    foreach (TrainNavigationLane lane in lanes)
                        remaining += LaneMeters(em, lane.m_Lane, lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                }
            }
            if (em.HasBuffer<PathElement>(vehicle))
            {
                DynamicBuffer<PathElement> route = em.GetBuffer<PathElement>(vehicle, true);
                for (int index = owner.m_ElementIndex; index < route.Length; index++)
                {
                    PathElement element = route[index];
                    remaining += LaneMeters(em, element.m_Target, element.m_TargetDelta.y - element.m_TargetDelta.x);
                }
            }
            return null;
        }

        private static float LaneMeters(EntityManager em, Entity lane, float delta)
            => em.HasComponent<Curve>(lane) ? em.GetComponentData<Curve>(lane).m_Length * math.abs(delta) : 0f;

        public bool TryLineMeters(Entity line, out float meters)
        {
            if (m_LineMeters.TryGetValue(line, out meters)) return true;
            meters = 0f;
            EntityManager em = m_Port.EntityManager;
            if (line == Entity.Null || !em.HasBuffer<RouteSegment>(line)) return false;
            DynamicBuffer<RouteSegment> segments = em.GetBuffer<RouteSegment>(line, true);
            if (segments.Length == 0) return false;
            for (int i = 0; i < segments.Length; i++)
            {
                Entity segment = segments[i].m_Segment;
                if (!em.HasComponent<PathInformation>(segment)) return false;
                float distance = em.GetComponentData<PathInformation>(segment).m_Distance;
                if (!(distance > 0f)) return false;
                meters += distance;
            }
            m_LineMeters[line] = meters;
            return true;
        }

        public void RefreshMonitor(MonitorChange change)
        {
            if (!change.Changed || m_Port.IsLinePending(change.Line)
                || !m_Profiles.TryGetValue(change.Line, out LineTimeProfileHeader profile)
                || !m_Port.EntityManager.HasBuffer<RouteWaypoint>(change.Line)) return;
            RebuildEta(change.Line, m_Port.EntityManager.GetBuffer<RouteWaypoint>(change.Line, true), ref profile);
            m_Profiles[change.Line] = profile;
        }

        public void RebuildAll()
        {
            m_Tried.Clear();
            using var lines = m_Profiles.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (!m_Port.EntityManager.Exists(line) || m_Port.IsLinePending(line)
                    || !m_Port.EntityManager.HasBuffer<RouteWaypoint>(line)) continue;
                LineTimeProfileHeader profile = m_Profiles[line];
                DynamicBuffer<RouteWaypoint> waypoints = m_Port.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (waypoints.Length != profile.m_Count) continue;
                RebuildEta(line, waypoints, ref profile);
                m_Profiles[line] = profile;
            }
        }

        private void RebuildEta(Entity line, DynamicBuffer<RouteWaypoint> waypoints, ref LineTimeProfileHeader profile)
        {
            int offset = profile.m_Offset;
            if (IsBus(line))
            {
                for (int i = 0; i < profile.m_Count; i++)
                {
                    m_EtaSeg[offset + i] = m_SegmentFrames[offset + i];
                    m_EtaDwell[offset + i] = i == 0 ? 0f : m_StopFrames[offset + i];
                }
                profile.m_EtaSource = (byte)EtaSource.Bus;
            }
            else
            {
                // 全部站间都有样本后整线切换，原剖面保留给轨道、待避与规划器。
                bool monitor = TryMonitorLayout(line, profile.m_Count, out LineStopLayout layout);
                if (monitor)
                {
                    for (int i = 0; i < layout.StopCount; i++)
                    {
                        if (!m_Monitor.TryAverage(line, i, out _)) { monitor = false; break; }
                    }
                }
                Entity prefab = m_Port.EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                TransportLineData data = m_Port.EntityManager.GetComponentData<TransportLineData>(prefab);
                for (int i = 0; i < profile.m_Count; i++)
                {
                    m_EtaSeg[offset + i] = m_SegmentFrames[offset + i];
                    m_EtaDwell[offset + i] = i == 0 ? 0f : monitor
                        ? MonitorDwell(line, waypoints, i, data) : Stop(line, waypoints, i, data);
                }
                if (monitor)
                {
                    for (int i = 0; i < layout.StopCount; i++)
                    {
                        int start = layout[i].WaypointIndex;
                        int end = layout[(i + 1) % layout.StopCount].WaypointIndex;
                        float raw = 0f;
                        int count = 0;
                        int cursor = start;
                        do { raw += m_SegmentFrames[offset + cursor]; count++; cursor = (cursor + 1) % profile.m_Count; } while (cursor != end);
                        m_Monitor.TryAverage(line, i, out float average);
                        cursor = start;
                        do
                        {
                            m_EtaSeg[offset + cursor] = raw > 0f ? average * m_SegmentFrames[offset + cursor] / raw : average / count;
                            cursor = (cursor + 1) % profile.m_Count;
                        } while (cursor != end);
                    }
                }
                profile.m_EtaSource = (byte)(monitor ? EtaSource.Monitor : EtaSource.Path);
            }
            RebuildEtaLeft(profile);
        }

        private void RebuildEtaLeft(LineTimeProfileHeader profile)
        {
            int offset = profile.m_Offset;
            float left = 0f;
            for (int i = profile.m_Count - 1; i >= 0; i--)
            {
                left = m_EtaSeg[offset + i] + (i + 1 < profile.m_Count ? m_EtaDwell[offset + i + 1] : 0f) + left;
                m_EtaLeft[offset + i] = left;
            }
        }

        private bool TryMonitorLayout(Entity line, int waypointCount, out LineStopLayout layout)
        {
            if (!m_Layouts.TryGetValue(line, out layout)
                && m_Invalidator.TryGetStableSignalInputs(line, out _, out layout)
                && layout.WaypointCount == waypointCount)
                m_Layouts[line] = layout;
            return layout != null
                && layout.WaypointCount == waypointCount
                && m_Monitor.TryState(line, layout.StopSig, out MonitorAverageState state) && state.Ready;
        }

        private float MonitorDwell(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int index, TransportLineData data)
        {
            if (!(StopBase(waypoints[index].m_Waypoint, data) > 0f)) return 0f;
            if (m_Port.TryObservedWaypointStopFrames(line, index, out float dwell) && dwell > 0f) return dwell;
            int minutes = m_Port.DwellMinutes(line);
            return minutes > 0 ? m_Port.ClockSnapshot().ToFramesCeil(minutes) : StopBase(waypoints[index].m_Waypoint, data);
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
            return GetCore(line, wps, out profile, false);
        }

        internal bool GetForStructure(Entity line, DynamicBuffer<RouteWaypoint> wps, out LineTimeProfileHeader profile)
        {
            return GetCore(line, wps, out profile, true);
        }

        private bool GetCore(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            out LineTimeProfileHeader profile,
            bool allowPending)
        {
            profile = default;
            if (!allowPending
                && m_Port.IsLinePending != null
                && m_Port.IsLinePending(line))
                return false;
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
            RebuildEta(line, wps, ref profile);
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
            m_Tried.Remove(line);
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
            m_Layouts.Remove(line);
            m_LineMeters.Remove(line);
            m_BusLines.Remove(line);
            m_Tried.Remove(line);
            m_BusMissingSeg.Remove(line);
            if (line == Entity.Null || !m_Profiles.TryGetValue(line, out LineTimeProfileHeader profile))
                return;

            m_Profiles.Remove(line);
            Release(profile.m_Offset, profile.m_Count);
        }

        internal LineTimesChangeResult ApplyStructure(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTimesPlanKind kind,
            LineStopLayout newLayout,
            LineIntervalImpact impact)
        {
            if (line == Entity.Null)
                return new LineTimesChangeResult(kind, false);

            m_LineMeters.Remove(line);
            if (newLayout != null) m_Layouts[line] = newLayout;

            if (kind == LineTimesPlanKind.Remove)
            {
                bool hadProfile = m_Profiles.ContainsKey(line);
                InvalidateLine(line);
                return new LineTimesChangeResult(kind, hadProfile);
            }

            if (waypoints.Length == 0 || !m_Port.EntityManager.HasBuffer<RouteSegment>(line))
                return new LineTimesChangeResult(LineTimesPlanKind.Remove, false);

            if (kind == LineTimesPlanKind.Rebuild)
            {
                InvalidateLine(line);
                if (newLayout != null) m_Layouts[line] = newLayout;
                return new LineTimesChangeResult(kind, GetForStructure(line, waypoints, out _));
            }

            if (newLayout == null
                || impact == null
                || !impact.IsValid
                || !m_Profiles.TryGetValue(line, out LineTimeProfileHeader profile)
                || profile.m_Count != waypoints.Length)
            {
                InvalidateLine(line);
                if (newLayout != null) m_Layouts[line] = newLayout;
                return new LineTimesChangeResult(
                    LineTimesPlanKind.Rebuild,
                    GetForStructure(line, waypoints, out _));
            }

            DynamicBuffer<RouteSegment> segments = m_Port.EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length
                || profile.m_Offset < 0
                || profile.m_Offset + profile.m_Count > m_SegmentFrames.Length)
            {
                InvalidateLine(line);
                if (newLayout != null) m_Layouts[line] = newLayout;
                return new LineTimesChangeResult(
                    LineTimesPlanKind.Rebuild,
                    GetForStructure(line, waypoints, out _));
            }

            bool[] affectedSegments = new bool[profile.m_Count];
            if (!MarkAffectedSegments(newLayout, impact, affectedSegments))
            {
                InvalidateLine(line);
                if (newLayout != null) m_Layouts[line] = newLayout;
                return new LineTimesChangeResult(
                    LineTimesPlanKind.Rebuild,
                    GetForStructure(line, waypoints, out _));
            }

            float[] refreshed = new float[profile.m_Count];
            for (int i = 0; i < affectedSegments.Length; i++)
            {
                if (!affectedSegments[i])
                    continue;
                if (!TryRawFrames(line, i, out refreshed[i]))
                {
                    InvalidateLine(line);
                    if (newLayout != null) m_Layouts[line] = newLayout;
                    return new LineTimesChangeResult(
                        LineTimesPlanKind.Rebuild,
                        GetForStructure(line, waypoints, out _));
                }
            }

            for (int i = 0; i < affectedSegments.Length; i++)
            {
                if (affectedSegments[i])
                    m_SegmentFrames[profile.m_Offset + i] = refreshed[i];
            }

            profile.m_Signature = Signature(waypoints, segments);
            profile.m_BaseLoopFrames = 0f;
            for (int i = 0; i < profile.m_Count; i++)
            {
                profile.m_BaseLoopFrames += m_SegmentFrames[profile.m_Offset + i];
                if (i != 0)
                    profile.m_BaseLoopFrames += m_StopFrames[profile.m_Offset + i];
            }
            RebuildEta(line, waypoints, ref profile);
            m_Profiles[line] = profile;
            return new LineTimesChangeResult(kind, true);
        }

        private static bool MarkAffectedSegments(
            LineStopLayout layout,
            LineIntervalImpact impact,
            bool[] affectedSegments)
        {
            if (layout.StopCount < 2 || affectedSegments == null)
                return false;

            for (int intervalIndex = 0; intervalIndex < layout.StopCount; intervalIndex++)
            {
                if (!impact.IsNewAffected(intervalIndex))
                    continue;

                int start = layout[intervalIndex].WaypointIndex;
                int end = layout[(intervalIndex + 1) % layout.StopCount].WaypointIndex;
                if (start < 0 || start >= affectedSegments.Length
                    || end < 0 || end >= affectedSegments.Length)
                {
                    return false;
                }

                int cursor = start;
                int guard = 0;
                while (cursor != end && guard++ <= affectedSegments.Length)
                {
                    affectedSegments[cursor] = true;
                    cursor = (cursor + 1) % affectedSegments.Length;
                }
                if (cursor != end)
                    return false;
            }

            return true;
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
            RebuildEta(line, waypoints, ref profile);
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
                m_EtaSeg.Add(0f);
                m_EtaDwell.Add(0f);
                m_EtaLeft.Add(0f);
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
                m_EtaSeg[offset + i] = 0f;
                m_EtaDwell[offset + i] = 0f;
                m_EtaLeft[offset + i] = 0f;
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
            if (line == Entity.Null) return false;
            if (!m_BusLines.TryGetValue(line, out bool bus))
            {
                bus = m_Port.ResolveMode(line) == TransitMode.Bus;
                m_BusLines[line] = bus;
            }
            return bus;
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
            float etaStopFrames;
            if (isBus)
            {
                updatedStopFrames = BusStop(line, waypointIndex);
                etaStopFrames = updatedStopFrames;
            }
            else
            {
                if (!m_Port.EntityManager.HasComponent<PrefabRef>(line))
                    return;
                Entity prefab = m_Port.EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (!m_Port.EntityManager.HasComponent<TransportLineData>(prefab))
                    return;
                TransportLineData data = m_Port.EntityManager.GetComponentData<TransportLineData>(prefab);
                updatedStopFrames = Stop(line, waypoints, waypointIndex, data);
                etaStopFrames = profile.m_EtaSource == (byte)EtaSource.Monitor
                    ? MonitorDwell(line, waypoints, waypointIndex, data)
                    : updatedStopFrames;
            }
            if (math.abs(previousStopFrames - updatedStopFrames) >= 0.01f)
            {
                m_StopFrames[stopOffset] = updatedStopFrames;
                if (waypointIndex != 0)
                {
                    profile.m_BaseLoopFrames = math.max(
                        0f,
                        profile.m_BaseLoopFrames + updatedStopFrames - previousStopFrames);
                }
            }
            m_EtaDwell[stopOffset] = waypointIndex == 0 ? 0f : isBus ? m_StopFrames[stopOffset] : etaStopFrames;
            RebuildEtaLeft(profile);
            m_Profiles[line] = profile;
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

        internal bool TryMonitorLoopFrames(Entity line, out float frames)
        {
            frames = 0f;
            if (IsBus(line)
                || !TryEtaProfile(line, out LineTimeProfileHeader profile)
                || profile.m_EtaSource != (byte)EtaSource.Monitor)
            {
                return false;
            }

            frames = m_EtaLeft[profile.m_Offset];
            return frames > 0f;
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
