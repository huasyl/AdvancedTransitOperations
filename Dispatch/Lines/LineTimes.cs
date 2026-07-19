using Game.Objects;
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
        private NativeList<float> m_SegmentFrames;
        private NativeList<float> m_StopFrames;

        public LineTimes(LineTimesPort port)
        {
            m_Port = port;
        }

        public void Init()
        {
            m_Profiles = new NativeHashMap<Entity, LineTimeProfileHeader>(64, Allocator.Persistent);
            m_SegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_StopFrames = new NativeList<float>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_Profiles.IsCreated) m_Profiles.Dispose();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Dispose();
            if (m_StopFrames.IsCreated) m_StopFrames.Dispose();
        }

        public void Clear()
        {
            if (m_Profiles.IsCreated) m_Profiles.Clear();
            if (m_SegmentFrames.IsCreated) m_SegmentFrames.Clear();
            if (m_StopFrames.IsCreated) m_StopFrames.Clear();
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

            if (!entityManager.HasComponent<PrefabRef>(line)) return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!entityManager.HasComponent<TransportLineData>(prefab)) return false;
            TransportLineData prefabLineData = entityManager.GetComponentData<TransportLineData>(prefab);

            int count = wps.Length;
            float baseLoopFrames = 0f;
            int offset = m_SegmentFrames.Length;

            for (int i = 0; i < count; i++)
            {
                m_SegmentFrames.Add(0f);
                m_StopFrames.Add(0f);
            }

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

            if (baseLoopFrames <= 0f) return false;

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
                float scale = Scale(vehicle, profile.m_BaseLoopFrames);
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
                float scale = Scale(v, profile.m_BaseLoopFrames);

                if (m_Port.TryRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                    return Remain(profile, nextWaypointIndex, segmentPosition) * scale;

                int cachedWpIdx = m_Port.CachedWaypointIndex(v);
                float cachedWaypointEstimate = CachedRemain(profile, cachedWpIdx);
                if (cachedWaypointEstimate != float.MaxValue)
                    return cachedWaypointEstimate * scale;
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

        public float Scale(Entity v, float baseLoopFrames)
        {
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
