using System;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal sealed class VehicleCache
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Func<Entity, float> m_ReadLap;
        private readonly Func<Entity, float> m_ReadDist;
        private readonly TryProgress m_Progress;

        public delegate bool TryProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);

        public VehicleCache(
            DispatchRuntimeSystem runtime,
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
        }

        public bool Restore(Entity v, Entity line, bool allowRunningRestore)
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
                    m_Runtime.m_RuntimeController.RestoreHold(v, cachedTarget);

                    if (m_Runtime.EntityManager.HasComponent<PublicTransport>(v))
                    {
                        PublicTransport pt = m_Runtime.EntityManager.GetComponentData<PublicTransport>(v);
                        pt.m_DepartureFrame = m_Runtime.m_SimulationSystem.frameIndex + 99999;
                        m_Runtime.EntityManager.SetComponentData(v, pt);
                    }
                    if (RtLog.VerboseEnabled)
                    {
                        m_Runtime.log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                            + " Holding target=" + (cachedTarget >= 0 ? DispatchRuntimeSystem.SlotStr(cachedTarget) : "-"));
                    }
                    return true;
                }

                if (cachedState == VehicleState.Running)
                {
                    if (!allowRunningRestore)
                        return false;

                    m_Runtime.m_RuntimeController.RestoreRun(v);

                    float cachedLapDist = m_ReadDist(line);
                    bool restoredLapStart = false;
                    if (m_Runtime.EntityManager.HasComponent<Odometer>(v))
                    {
                        float currentOdo = m_Runtime.EntityManager.GetComponentData<Odometer>(v).m_Distance;
                        m_Runtime.m_ObsPersist.StartLap(
                            v,
                            cachedLapDist > 0f ? currentOdo - cachedLapDist : currentOdo,
                            m_Runtime.m_SimulationSystem.frameIndex);
                        restoredLapStart = true;
                    }
                    else
                    {
                        m_Runtime.m_ObsPersist.ClearLapStart(v);
                    }
                    m_Runtime.m_ObsPersist.SetLapFrames(v, 0);
                    m_Runtime.m_BVMisfire.Remove(v);
                    m_Runtime.m_BVMisfireStartFrame.Remove(v);
                    m_Runtime.m_ObsPersist.MarkLapRestored(v);
                    if (RtLog.VerboseEnabled)
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

        public bool RestoreRun(Entity v, Entity line, DynamicBuffer<RouteWaypoint> wps, string initReason)
        {
            float cachedLapFrames = m_ReadLap(line);
            if (cachedLapFrames <= 0f) return false;
            if (!m_Progress(v, out int nextWaypointIndex, out float segmentPosition)) return false;

            m_Runtime.m_RuntimeController.RestoreRun(v);

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

            if (RtLog.VerboseEnabled)
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
    }
}
