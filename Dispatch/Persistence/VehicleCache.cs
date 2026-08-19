using System;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class VehicleCache
    {
        internal delegate PublicTransport ReadPublicTransport(Entity vehicle);
        internal delegate void CommitPublicTransport(Entity vehicle, PublicTransport value);

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Func<Entity, float> m_ReadLap;
        private readonly Func<Entity, float> m_ReadDist;
        private readonly TryProgress m_Progress;

        public delegate bool TryProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);

        public VehicleCache(
            ModRuntimeHostSystem runtime,
            Func<Entity, float> readLap,
            Func<Entity, float> readDist,
            TryProgress progress)
        {
            m_Runtime = runtime;
            m_ReadLap = readLap;
            m_ReadDist = readDist;
            m_Progress = progress;
        }

        public void Ensure()
        {
            if (m_Runtime.m_VehicleCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<VehicleStateCacheElement>(city))
            {
                m_Runtime.EntityManager.AddBuffer<VehicleStateCacheElement>(city);
                m_Runtime.log.Info("[缓存] 已在城市实体上创建 VehicleStateCacheElement Buffer");
            }
            if (!m_Runtime.EntityManager.HasBuffer<TimedPlanCacheElement>(city))
                m_Runtime.EntityManager.AddBuffer<TimedPlanCacheElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<TimedStopCacheElement>(city))
                m_Runtime.EntityManager.AddBuffer<TimedStopCacheElement>(city);
            m_Runtime.m_VehicleCacheBufferReady = true;
        }

        public void Save()
        {
            if (!m_Runtime.m_VehicleCacheBufferReady) return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!m_Runtime.EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return;

            DynamicBuffer<VehicleStateCacheElement> buf = m_Runtime.EntityManager.GetBuffer<VehicleStateCacheElement>(city);
            buf.Clear();

            NativeArray<Entity> keys = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                Entity v = keys[i];
                VehicleState st = m_Runtime.m_VehicleView.GetState(v);
                if (st == VehicleState.Retiring) continue;

                int targetMin = m_Runtime.m_VehicleView.TryGetTarget(v, out int tm) ? tm : -1;
                buf.Add(new VehicleStateCacheElement
                {
                    m_VehicleEntity = v,
                    m_State = st,
                    m_TargetMin = targetMin
                });
            }
            keys.Dispose();

            DynamicBuffer<TimedPlanCacheElement> planBuffer =
                m_Runtime.EntityManager.GetBuffer<TimedPlanCacheElement>(city);
            DynamicBuffer<TimedStopCacheElement> stopBuffer =
                m_Runtime.EntityManager.GetBuffer<TimedStopCacheElement>(city);
            planBuffer.Clear();
            stopBuffer.Clear();
            foreach (TimedPlanSnapshot snapshot in m_Runtime.m_StopRuntime.TimedPlans())
            {
                if (!m_Runtime.m_VehicleView.TryGetState(snapshot.Vehicle, out VehicleState state)
                    || state != VehicleState.Running
                    || !m_Runtime.m_VehicleView.TryGetLine(snapshot.Vehicle, out Entity line)
                    || line != snapshot.Line)
                {
                    continue;
                }
                if (snapshot.ActiveStopOrder >= 0
                    && (double.IsNaN(snapshot.ArrivalWaitMinutes)
                        || double.IsInfinity(snapshot.ArrivalWaitMinutes)
                        || snapshot.ArrivalWaitMinutes < 0d
                        || snapshot.ArrivalWaitMinutes > 5d))
                {
                    continue;
                }

                planBuffer.Add(new TimedPlanCacheElement
                {
                    m_Version = 2,
                    m_VehicleEntity = snapshot.Vehicle,
                    m_LineEntity = snapshot.Line,
                    m_RowId = snapshot.RowId,
                    m_StopSig = snapshot.StopSig,
                    m_ServiceDateTicks = snapshot.ServiceDate.Date.Ticks,
                    m_SlotMinute = snapshot.SlotMinute,
                    m_NextStopOrder = snapshot.NextStopOrder,
                    m_ActiveStopOrder = snapshot.ActiveStopOrder,
                    m_StopCount = snapshot.Stops.Length,
                    m_CanBypass = snapshot.CanBypass ? (byte)1 : (byte)0,
                    m_ArrivalWaitMinutes = snapshot.ArrivalWaitMinutes,
                    m_ClockTicksPerDay = snapshot.ClockTicksPerDay
                });
                for (int stopIndex = 0; stopIndex < snapshot.Stops.Length; stopIndex++)
                {
                    TimedStop stop = snapshot.Stops[stopIndex];
                    stopBuffer.Add(new TimedStopCacheElement
                    {
                        m_Version = 2,
                        m_VehicleEntity = snapshot.Vehicle,
                        m_Order = stopIndex,
                        m_StopKey = stop.StopKey,
                        m_Arrive = stop.Arrive,
                        m_Depart = stop.Depart,
                        m_WaypointIndex = snapshot.WaypointIndices[stopIndex]
                    });
                }
            }
        }

        public bool Restore(
            Entity v,
            Entity line,
            bool allowRunningRestore,
            ReadPublicTransport readPublicTransport = null,
            CommitPublicTransport commitPublicTransport = null,
            bool registryOnly = false)
        {
            if (!m_Runtime.m_VehicleCacheBufferReady) return false;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null) return false;
            if (!m_Runtime.EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return false;

            DynamicBuffer<VehicleStateCacheElement> buf = m_Runtime.EntityManager.GetBuffer<VehicleStateCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_VehicleEntity != v) continue;

                VehicleState cachedState = buf[i].m_State;
                int cachedTarget = buf[i].m_TargetMin;

                if (cachedState == VehicleState.Holding)
                {
                    m_Runtime.m_StopRuntime.ClearTimedPlan(v);
                    m_Runtime.m_RuntimeEngine.RestoreHold(v, cachedTarget);

                    if (!registryOnly && m_Runtime.EntityManager.HasComponent<PublicTransport>(v))
                    {
                        uint frame = m_Runtime.m_SimulationSystem.frameIndex;
                        PublicTransport pt = readPublicTransport != null
                            ? readPublicTransport(v)
                            : m_Runtime.EntityManager.GetComponentData<PublicTransport>(v);
                        pt.m_DepartureFrame = frame + 99999;
                        if (commitPublicTransport != null)
                            commitPublicTransport(v, pt);
                        else
                            m_Runtime.EntityManager.SetComponentData(v, pt);
                    }
                    if (RtLog.VerboseEnabled && !m_Runtime.m_VehicleRegistry.IsSilentRestore)
                    {
                        m_Runtime.log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                            + " Holding target=" + (cachedTarget >= 0 ? ModRuntimeHostSystem.SlotStr(cachedTarget) : "-"));
                    }
                    return true;
                }

                if (cachedState == VehicleState.Running)
                {
                    if (!allowRunningRestore)
                        return false;

                    m_Runtime.m_RuntimeEngine.RestoreRun(v);
                    RestoreTimed(v, line);

                    float cachedLapDist = -1f;
                    bool restoredLapStart = false;
                    if (!registryOnly && m_Runtime.EntityManager.HasComponent<Odometer>(v))
                    {
                        cachedLapDist = m_ReadDist(line);
                        float currentOdo = m_Runtime.EntityManager.GetComponentData<Odometer>(v).m_Distance;
                        m_Runtime.m_ObsPersist.StartLap(
                            v,
                            cachedLapDist > 0f ? currentOdo - cachedLapDist : currentOdo,
                            m_Runtime.m_SimulationSystem.frameIndex);
                        restoredLapStart = true;
                    }
                    else if (!registryOnly)
                    {
                        m_Runtime.m_ObsPersist.ClearLapStart(v);
                    }
                    if (!registryOnly)
                    {
                        m_Runtime.m_ObsPersist.SetLapFrames(v, 0);
                        m_Runtime.m_ObsPersist.MarkLapRestored(v);
                    }
                    if (RtLog.VerboseEnabled && !m_Runtime.m_VehicleRegistry.IsSilentRestore)
                    {
                        m_Runtime.log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                            + " Running lapDist=" + cachedLapDist.ToString("F1")
                            + " lapStart=" + (restoredLapStart ? "ok" : "missing-odometer")
                            + " startFrame=" + (restoredLapStart ? m_Runtime.m_SimulationSystem.frameIndex.ToString() : "-"));
                    }

                    return true;
                }

                return false;
            }
            return false;
        }

        private bool RestoreTimed(Entity vehicle, Entity line)
        {
            m_Runtime.m_StopRuntime.ClearTimedPlan(vehicle);
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<TimedPlanCacheElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<TimedStopCacheElement>(city))
            {
                return false;
            }

            DynamicBuffer<TimedPlanCacheElement> plans =
                m_Runtime.EntityManager.GetBuffer<TimedPlanCacheElement>(city, true);
            TimedPlanCacheElement header = default;
            int headerCount = 0;
            for (int i = 0; i < plans.Length; i++)
            {
                if (plans[i].m_VehicleEntity != vehicle)
                    continue;
                header = plans[i];
                headerCount++;
            }

            if (headerCount != 1
                || header.m_Version != 2
                || header.m_LineEntity != line
                || header.m_StopCount <= 0
                || header.m_StopCount > 512
                || header.m_SlotMinute < 0
                || header.m_SlotMinute >= 1440
                || header.m_CanBypass > 1
                || header.m_ClockTicksPerDay <= 0
                || string.IsNullOrEmpty(header.m_RowId.ToString())
                || string.IsNullOrEmpty(header.m_StopSig.ToString())
                || header.m_ServiceDateTicks <= DateTime.MinValue.Ticks
                || header.m_ServiceDateTicks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            TimedStop[] stops = new TimedStop[header.m_StopCount];
            int[] waypoints = new int[header.m_StopCount];
            bool[] seen = new bool[header.m_StopCount];
            int count = 0;
            DynamicBuffer<TimedStopCacheElement> stopBuffer =
                m_Runtime.EntityManager.GetBuffer<TimedStopCacheElement>(city, true);
            for (int i = 0; i < stopBuffer.Length; i++)
            {
                TimedStopCacheElement element = stopBuffer[i];
                if (element.m_VehicleEntity != vehicle)
                    continue;
                if (element.m_Version != 2
                    || element.m_Order < 0
                    || element.m_Order >= stops.Length
                    || seen[element.m_Order]
                    || string.IsNullOrEmpty(element.m_StopKey.ToString())
                    || element.m_WaypointIndex < 0
                    || element.m_Arrive < -1
                    || element.m_Arrive >= 48 * 60
                    || element.m_Depart < -1
                    || element.m_Depart >= 48 * 60)
                {
                    return false;
                }

                seen[element.m_Order] = true;
                count++;
                stops[element.m_Order] = new TimedStop
                {
                    StopKey = element.m_StopKey.ToString(),
                    Arrive = element.m_Arrive,
                    Depart = element.m_Depart
                };
                waypoints[element.m_Order] = element.m_WaypointIndex;
            }

            if (count != stops.Length
                || !m_Runtime.m_LineView.TryStopLayout(line, out string stopSig, out int[] currentWaypoints))
            {
                return false;
            }

            TimedStopPlan plan = new TimedStopPlan
                {
                    Line = line,
                    RowId = header.m_RowId.ToString(),
                    StopSig = header.m_StopSig.ToString(),
                    ServiceDate = new DateTime(header.m_ServiceDateTicks).Date,
                    SlotMinute = header.m_SlotMinute,
                    Stops = stops,
                    WaypointIndices = waypoints,
                    NextStopOrder = header.m_NextStopOrder,
                    ActiveStopOrder = header.m_ActiveStopOrder,
                    CanBypass = header.m_CanBypass != 0
                };
            TimedPlanSnapshot snapshot = new TimedPlanSnapshot(
                vehicle,
                plan,
                header.m_ArrivalWaitMinutes,
                header.m_ClockTicksPerDay);
            return m_Runtime.m_StopRuntime.RestoreTimedPlan(snapshot, stopSig, currentWaypoints);
        }

        public bool RestoreRun(Entity v, Entity line, DynamicBuffer<RouteWaypoint> wps, string initReason)
        {
            float cachedLapFrames = m_ReadLap(line);
            if (cachedLapFrames <= 0f) return false;
            if (!m_Progress(v, out int nextWaypointIndex, out float segmentPosition)) return false;

            m_Runtime.m_RuntimeEngine.RestoreRun(v);
            RestoreTimed(v, line);

            float segmentBase = nextWaypointIndex == 0 ? (wps.Length - 1) : (nextWaypointIndex - 1);
            float progress = (segmentBase + math.saturate(segmentPosition)) / math.max(1, wps.Length);
            progress = math.clamp(progress, 0f, 0.999f);

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            uint estimatedStartFrame = nowFrame > (uint)(cachedLapFrames * progress)
                ? nowFrame - (uint)math.round(cachedLapFrames * progress)
                : 0u;
            m_Runtime.m_ObsPersist.SetLapStartFrame(v, estimatedStartFrame);
            m_Runtime.m_ObsPersist.SetLapFrames(v, (uint)cachedLapFrames);

            if (m_Runtime.EntityManager.HasComponent<Odometer>(v))
            {
                float currentOdo = m_Runtime.EntityManager.GetComponentData<Odometer>(v).m_Distance;
                float cachedLapDistance = m_ReadDist(line);
                if (cachedLapDistance > 0f)
                    m_Runtime.m_ObsPersist.SetLapStartOdo(v, currentOdo - cachedLapDistance * progress);
                else
                    m_Runtime.m_ObsPersist.SetLapStartOdo(v, currentOdo);
            }

            if (RtLog.VerboseEnabled && !m_Runtime.m_VehicleRegistry.IsSilentRestore)
            {
                m_Runtime.log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                    + " Running进度恢复"
                    + " progress=" + progress.ToString("F2")
                    + " wp=" + nextWaypointIndex
                    + " seg=" + segmentPosition.ToString("F2")
                    + " lapFrames=" + ((uint)cachedLapFrames).ToString()
                    + " startFrame=" + estimatedStartFrame
                    + " from=" + initReason);
            }
            return true;
        }

        public void SeedStartupRunningLapStart(Entity vehicle, Entity line)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                || state != VehicleState.Running
                || !m_Runtime.EntityManager.HasComponent<Odometer>(vehicle))
            {
                return;
            }

            float currentOdometer = m_Runtime.EntityManager.GetComponentData<Odometer>(vehicle).m_Distance;
            if (float.IsNaN(currentOdometer) || float.IsInfinity(currentOdometer) || currentOdometer < 0f)
                return;

            float cachedLapDistance = m_ReadDist(line);
            float lapStartOdometer = cachedLapDistance > 0f
                && !float.IsNaN(cachedLapDistance)
                && !float.IsInfinity(cachedLapDistance)
                && cachedLapDistance <= currentOdometer
                    ? currentOdometer - cachedLapDistance
                    : currentOdometer;
            m_Runtime.m_ObsPersist.SetLapStartOdo(vehicle, lapStartOdometer);
        }

    }
}
