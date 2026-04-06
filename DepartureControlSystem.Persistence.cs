using System.Collections.Generic;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void EnsureStopDwellObservationBuffer()
        {
            if (!IsStopDwellObservationPersistenceEnabled())
                return;

            if (m_StopDwellObservationBufferReady)
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!EntityManager.HasBuffer<StopDwellObservationElement>(city))
                EntityManager.AddBuffer<StopDwellObservationElement>(city);

            m_StopDwellObservationBufferReady = true;
        }

        private void RestoreStopDwellObservationsFromBuffer()
        {
            if (!IsStopDwellObservationPersistenceEnabled())
                return;

            if (m_StopDwellObservationCacheLoaded || !m_StopDwellObservationBufferReady)
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<StopDwellObservationElement>(city))
                return;

            m_WaypointStopDwellObservations.Clear();
            var buffer = EntityManager.GetBuffer<StopDwellObservationElement>(city, true);
            int restoredCount = 0;
            int skippedSignatureMismatchCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                StopDwellObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null
                    || entry.m_WaypointIndex < 0
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    continue;
                }

                if (!TryGetStopDwellObservationProfileSignature(entry.m_LineEntity, out ulong currentSignature)
                    || currentSignature != entry.m_ProfileSignature)
                {
                    skippedSignatureMismatchCount++;
                    continue;
                }

                m_WaypointStopDwellObservations[MakeLineWaypointStopObservationKey(entry.m_LineEntity, entry.m_WaypointIndex)] =
                    new StopDwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount)
                    };
                restoredCount++;
            }

            m_StopDwellObservationCacheLoaded = true;
            log.Info("[恢复] StopDwellObservations buffer=" + buffer.Length
                + " restored=" + restoredCount
                + " skippedSignatureMismatch=" + skippedSignatureMismatchCount);
        }

        private void FlushStopDwellObservation(Entity line, int waypointIndex, StopDwellObservation observation)
        {
            if (!IsStopDwellObservationPersistenceEnabled())
                return;

            if (line == Entity.Null
                || waypointIndex < 0
                || !(observation.AverageFrames > 0f)
                || observation.SampleCount <= 0
                || !m_StopDwellObservationBufferReady)
            {
                return;
            }

            if (!TryGetStopDwellObservationProfileSignature(line, out ulong profileSignature))
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<StopDwellObservationElement>(city))
                return;

            var buffer = EntityManager.GetBuffer<StopDwellObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_WaypointIndex != waypointIndex)
                    continue;

                buffer[i] = new StopDwellObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_WaypointIndex = waypointIndex,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount
                };
                return;
            }

            buffer.Add(new StopDwellObservationElement
            {
                m_LineEntity = line,
                m_ProfileSignature = profileSignature,
                m_WaypointIndex = waypointIndex,
                m_AverageFrames = observation.AverageFrames,
                m_SampleCount = observation.SampleCount
            });
        }

        private bool TryGetStopDwellObservationProfileSignature(Entity line, out ulong signature)
        {
            signature = 0UL;
            if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            var segmentBuffers = GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            signature = ComputeLineProfileSignature(waypoints, segments);
            return signature != 0UL;
        }

        private void EnsureTraversalSliceObservationBuffer()
        {
            if (!IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                EntityManager.AddBuffer<TraversalSliceObservationElement>(city);

            m_TraversalSliceObservationBufferReady = true;
        }

        private void RestoreTraversalSliceObservationsFromBuffer()
        {
            if (!IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (m_TraversalSliceObservationCacheLoaded)
                return;

            if (!m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            m_TraversalRunSliceObservations.Clear();
            var buffer = EntityManager.GetBuffer<TraversalSliceObservationElement>(city, true);
            int restoredCount = 0;
            int skippedSignatureMismatchCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                TraversalSliceObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_SliceIndex < 0)
                    continue;

                if (!TryGetTraversalSliceObservationProfileSignature(entry.m_LineEntity, out ulong currentSignature)
                    || currentSignature != entry.m_ProfileSignature)
                {
                    skippedSignatureMismatchCount++;
                    continue;
                }

                ulong key = MakeTraversalSliceObservationKey(entry.m_LineEntity, entry.m_SliceIndex);
                m_TraversalRunSliceObservations[key] = new TraversalSliceObservation(
                    entry.m_AverageFrames,
                    entry.m_FastBaselineFrames > 0f ? entry.m_FastBaselineFrames : entry.m_AverageFrames,
                    math.max(0, entry.m_SampleCount),
                    entry.m_LastObservedFrame);
                restoredCount++;
            }

            m_TraversalSliceObservationCacheLoaded = true;
            log.Info("[恢复] TraversalSliceObservations buffer=" + buffer.Length
                + " restored=" + restoredCount
                + " skippedSignatureMismatch=" + skippedSignatureMismatchCount);
        }

        private void FlushTraversalSliceObservation(Entity line, int sliceIndex, TraversalSliceObservation observation)
        {
            if (!IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (line == Entity.Null || sliceIndex < 0 || observation.SampleCount <= 0)
                return;

            if (!TryGetTraversalSliceObservationProfileSignature(line, out ulong profileSignature))
                return;

            if (!m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            var buffer = EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_SliceIndex != sliceIndex)
                    continue;

                buffer[i] = new TraversalSliceObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_SliceIndex = sliceIndex,
                    m_AverageFrames = observation.AverageFrames,
                    m_FastBaselineFrames = observation.FastBaselineFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                };
                return;
            }

            buffer.Add(new TraversalSliceObservationElement
            {
                m_LineEntity = line,
                m_ProfileSignature = profileSignature,
                m_SliceIndex = sliceIndex,
                m_AverageFrames = observation.AverageFrames,
                m_FastBaselineFrames = observation.FastBaselineFrames,
                m_SampleCount = observation.SampleCount,
                m_LastObservedFrame = observation.LastObservedFrame
            });
        }

        private bool TryGetTraversalSliceObservationProfileSignature(Entity line, out ulong signature)
        {
            signature = 0UL;
            if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            var segmentBuffers = GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            signature = ComputeLineProfileSignature(waypoints, segments);
            return signature != 0UL;
        }

        private void EnsureDispatchCacheBuffer()
        {
            if (m_DispatchCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city))
                EntityManager.AddBuffer<LineDispatchCacheElement>(city);
            if (!EntityManager.HasBuffer<LineDispatchHistoryElement>(city))
                EntityManager.AddBuffer<LineDispatchHistoryElement>(city);
            m_DispatchCacheBufferReady = true;
        }

        private void EnsureBypassStationBuffer()
        {
            if (m_BypassStationBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<BypassStationSettingElement>(city))
                EntityManager.AddBuffer<BypassStationSettingElement>(city);
            m_BypassStationBufferReady = true;
        }

        private void EnsureLineMileageBuffers()
        {
            if (m_LineMileageBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineMileageModelStateElement>(city))
                EntityManager.AddBuffer<LineMileageModelStateElement>(city);
            if (!EntityManager.HasBuffer<LineMileageAnchorElement>(city))
                EntityManager.AddBuffer<LineMileageAnchorElement>(city);
            if (!EntityManager.HasBuffer<LineCorridorStateElement>(city))
                EntityManager.AddBuffer<LineCorridorStateElement>(city);
            if (!EntityManager.HasBuffer<LineCorridorNodeElement>(city))
                EntityManager.AddBuffer<LineCorridorNodeElement>(city);
            m_LineMileageBufferReady = true;
        }

        private float ReadDispatchCache(Entity line)
        {
            if (!m_DispatchCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineDispatchCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_DepotToOriginFrames;
            }
            return 0f;
        }

        private void UpdateDispatchCache(Entity line, uint sampleFrames)
        {
            if (!m_DispatchCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city)) return;
            if (!EntityManager.HasBuffer<LineDispatchHistoryElement>(city)) return;

            var buf = EntityManager.GetBuffer<LineDispatchCacheElement>(city);
            var historyBuf = EntityManager.GetBuffer<LineDispatchHistoryElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity != line) continue;
                uint oldFrames = buf[i].m_DepotToOriginFrames;
                LineDispatchHistoryElement history = GetDispatchHistoryElement(historyBuf, line);
                LineDispatchHistoryElement updatedHistory = AppendDispatchSample(history, sampleFrames);
                uint newFrames = ComputeDispatchSampleAverage(ReadDispatchSamples(updatedHistory));
                buf[i] = new LineDispatchCacheElement
                {
                    m_LineEntity = line,
                    m_DepotToOriginFrames = newFrames
                };
                UpsertDispatchHistory(historyBuf, updatedHistory);
                float oldMinutes = oldFrames / (float)SIM_FRAMES_PER_MINUTE;
                float newMinutes = newFrames / (float)SIM_FRAMES_PER_MINUTE;
                log.Info("[出库缓存] 线路" + line.Index
                    + " 样本=" + (sampleFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                    + " 最近" + updatedHistory.m_SampleCount + "条均值" + newMinutes.ToString("F1") + "分钟"
                    + (oldFrames > 0 ? " 旧值" + oldMinutes.ToString("F1") + "分钟" : ""));
                return;
            }

            LineDispatchHistoryElement createdHistory = AppendDispatchSample(new LineDispatchHistoryElement
            {
                m_LineEntity = line
            }, sampleFrames);
            uint createdFrames = ComputeDispatchSampleAverage(ReadDispatchSamples(createdHistory));
            buf.Add(new LineDispatchCacheElement
            {
                m_LineEntity = line,
                m_DepotToOriginFrames = createdFrames
            });
            UpsertDispatchHistory(historyBuf, createdHistory);
            log.Info("[出库缓存新增] 线路" + line.Index
                + " 最近" + createdHistory.m_SampleCount + "条均值"
                + (createdFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟");
        }

        private static LineDispatchHistoryElement GetDispatchHistoryElement(DynamicBuffer<LineDispatchHistoryElement> historyBuf, Entity line)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity == line)
                    return historyBuf[i];
            }
            return new LineDispatchHistoryElement
            {
                m_LineEntity = line
            };
        }

        private static void UpsertDispatchHistory(DynamicBuffer<LineDispatchHistoryElement> historyBuf, LineDispatchHistoryElement history)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity != history.m_LineEntity) continue;
                historyBuf[i] = history;
                return;
            }
            historyBuf.Add(history);
        }

        private LineDispatchHistoryElement AppendDispatchSample(LineDispatchHistoryElement element, uint sampleFrames)
        {
            var samples = ReadDispatchSamples(element);
            samples.Add(sampleFrames);
            if (samples.Count > DISPATCH_SAMPLE_HISTORY_LIMIT)
                samples.RemoveAt(0);

            WriteDispatchSamples(ref element, samples);
            return element;
        }

        private static List<uint> ReadDispatchSamples(LineDispatchHistoryElement element)
        {
            var samples = new List<uint>(DISPATCH_SAMPLE_HISTORY_LIMIT);
            AddDispatchSampleIfValid(samples, element.m_Sample0);
            AddDispatchSampleIfValid(samples, element.m_Sample1);
            AddDispatchSampleIfValid(samples, element.m_Sample2);
            AddDispatchSampleIfValid(samples, element.m_Sample3);
            AddDispatchSampleIfValid(samples, element.m_Sample4);
            AddDispatchSampleIfValid(samples, element.m_Sample5);
            AddDispatchSampleIfValid(samples, element.m_Sample6);
            AddDispatchSampleIfValid(samples, element.m_Sample7);
            if (samples.Count > element.m_SampleCount)
                samples.RemoveRange((int)element.m_SampleCount, samples.Count - (int)element.m_SampleCount);
            return samples;
        }

        private static void AddDispatchSampleIfValid(List<uint> samples, uint value)
        {
            if (value > 0)
                samples.Add(value);
        }

        private static void WriteDispatchSamples(ref LineDispatchHistoryElement element, List<uint> samples)
        {
            element.m_SampleCount = (byte)math.min(samples.Count, DISPATCH_SAMPLE_HISTORY_LIMIT);
            element.m_Sample0 = samples.Count > 0 ? samples[0] : 0;
            element.m_Sample1 = samples.Count > 1 ? samples[1] : 0;
            element.m_Sample2 = samples.Count > 2 ? samples[2] : 0;
            element.m_Sample3 = samples.Count > 3 ? samples[3] : 0;
            element.m_Sample4 = samples.Count > 4 ? samples[4] : 0;
            element.m_Sample5 = samples.Count > 5 ? samples[5] : 0;
            element.m_Sample6 = samples.Count > 6 ? samples[6] : 0;
            element.m_Sample7 = samples.Count > 7 ? samples[7] : 0;
        }

        private static uint ComputeDispatchSampleAverage(List<uint> samples)
        {
            if (samples.Count == 0)
                return 0;

            double sum = 0d;
            for (int i = 0; i < samples.Count; i++)
                sum += samples[i];
            double mean = sum / samples.Count;
            double maxAccepted = mean * DISPATCH_SAMPLE_OUTLIER_FACTOR;

            double filteredSum = 0d;
            int filteredCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] > maxAccepted)
                    continue;
                filteredSum += samples[i];
                filteredCount++;
            }

            if (filteredCount == 0)
                return (uint)math.round((float)mean);

            return (uint)math.round((float)(filteredSum / filteredCount));
        }

        private void EnsureLapCacheBuffer()
        {
            if (m_LapCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city))
                EntityManager.AddBuffer<LineLapCacheElement>(city);
            m_LapCacheBufferReady = true;
        }

        private void FlushLineLapCache(Entity line)
        {
            if (!m_LapCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return;

            uint bestFrames = 0;
            float bestDist = 0f;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v0 = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(v0)) continue;
                    if (m_VehicleLapFrames.TryGetValue(v0, out uint lf) && lf > bestFrames)
                    {
                        bestFrames = lf;
                        m_VehicleLapDistance.TryGetValue(v0, out bestDist);
                    }
                }
            }
            if (bestFrames == 0) return;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                {
                    if (bestFrames > buf[i].m_MaxLapFrames)
                    {
                        buf[i] = new LineLapCacheElement
                        {
                            m_LineEntity = line,
                            m_MaxLapFrames = bestFrames,
                            m_MaxLapDistance = bestDist
                        };
                        log.Info("[缓存写入] 线路" + line.Index
                            + " 圈时=" + (bestFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟");
                    }
                    return;
                }
            }

            buf.Add(new LineLapCacheElement
            {
                m_LineEntity = line,
                m_MaxLapFrames = bestFrames,
                m_MaxLapDistance = bestDist
            });
            log.Info("[缓存新增] 线路" + line.Index
                + " 圈时=" + (bestFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟");
        }

        private float ReadLineLapCache(Entity line)
        {
            if (!m_LapCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapFrames;
            }
            return 0f;
        }

        private float ReadLineLapDistance(Entity line)
        {
            if (!m_LapCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapDistance;
            }
            return 0f;
        }

        private void EnsureVehicleCacheBuffer()
        {
            if (m_VehicleCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city))
            {
                EntityManager.AddBuffer<VehicleStateCacheElement>(city);
                log.Info("[缓存] 已在城市实体上创建 VehicleStateCacheElement Buffer");
            }
            m_VehicleCacheBufferReady = true;
        }

        private void FlushAllVehicleStates()
        {
            if (!m_VehicleCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return;

            var buf = EntityManager.GetBuffer<VehicleStateCacheElement>(city);
            buf.Clear();

            var keys = m_VehicleState.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                Entity v = keys[i];
                VehicleState st = m_VehicleState[v];
                if (st == VehicleState.Retiring) continue;

                int targetMin = m_VehicleTargetMin.TryGetValue(v, out int tm) ? tm : -1;
                buf.Add(new VehicleStateCacheElement
                {
                    m_VehicleEntity = v,
                    m_State = st,
                    m_TargetMin = targetMin
                });
            }
            keys.Dispose();
        }

        private bool TryRestoreVehicleState(Entity v, Entity line, bool allowRunningRestore = true)
        {
            if (!m_VehicleCacheBufferReady) return false;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return false;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return false;

            var buf = EntityManager.GetBuffer<VehicleStateCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_VehicleEntity != v) continue;

                VehicleState cachedState = buf[i].m_State;
                int cachedTarget = buf[i].m_TargetMin;

                if (cachedState == VehicleState.Holding)
                {
                    m_VehicleState[v] = VehicleState.Holding;
                    m_VehicleTargetMin[v] = cachedTarget;

                    if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v))
                    {
                        var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        pt.m_DepartureFrame = m_SimulationSystem.frameIndex + 99999;
                        EntityManager.SetComponentData(v, pt);
                    }
                    log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                        + " Holding target=" + (cachedTarget >= 0 ? SlotStr(cachedTarget) : "-"));
                    return true;
                }

                if (cachedState == VehicleState.Running)
                {
                    if (!allowRunningRestore)
                        return false;

                    m_VehicleState[v] = VehicleState.Running;
                    m_VehicleTargetMin[v] = -1;
                    m_VehicleCurrentSlot.Remove(v);

                    float cachedLapDist = 0f;
                    if (m_LapCacheBufferReady && EntityManager.HasBuffer<LineLapCacheElement>(city))
                    {
                        var lapBuf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
                        for (int j = 0; j < lapBuf.Length; j++)
                        {
                            if (lapBuf[j].m_LineEntity == line)
                            {
                                cachedLapDist = lapBuf[j].m_MaxLapDistance;
                                break;
                            }
                        }
                    }
                    bool restoredLapStart = false;
                    if (EntityManager.HasComponent<Odometer>(v))
                    {
                        float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
                        m_VehicleLapStartOdometer[v] = cachedLapDist > 0f
                            ? currentOdo - cachedLapDist
                            : currentOdo;
                        m_VehicleLapStartFrame[v] = m_SimulationSystem.frameIndex;
                        restoredLapStart = true;
                    }
                    else
                    {
                        m_VehicleLapStartOdometer.Remove(v);
                        m_VehicleLapStartFrame.Remove(v);
                    }
                    m_VehicleLapFrames[v] = 0;
                    m_VehicleLastLaunchFrame.Remove(v);
                    m_LaunchCooldownUntil.Remove(v);
                    m_BVMisfire.Remove(v);
                    m_BVMisfireStartFrame.Remove(v);
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    m_RestoredRunning.Add(v);
                    log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                        + " Running lapDist=" + cachedLapDist.ToString("F1")
                        + " lapStart=" + (restoredLapStart ? "ok" : "missing-odometer")
                        + " startFrame=" + (restoredLapStart ? m_SimulationSystem.frameIndex.ToString() : "-"));

                    return true;
                }

                return false;
            }
            return false;
        }

        private bool RestoreRunningContextFromProgress(Entity v, Entity line, DynamicBuffer<RouteWaypoint> wps, string initReason)
        {
            float cachedLapFrames = ReadLineLapCache(line);
            if (cachedLapFrames <= 0f) return false;
            if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition)) return false;

            m_VehicleCurrentSlot.Remove(v);
            m_VehicleTargetMin[v] = -1;
            m_VehicleLastLaunchFrame.Remove(v);
            m_LaunchCooldownUntil.Remove(v);
            m_OriginArrivalCandidateSinceFrame.Remove(v);

            float segmentBase = nextWaypointIndex == 0 ? (wps.Length - 1) : (nextWaypointIndex - 1);
            float progress = (segmentBase + math.saturate(segmentPosition)) / math.max(1, wps.Length);
            progress = math.clamp(progress, 0f, 0.999f);

            uint nowFrame = m_SimulationSystem.frameIndex;
            uint estimatedStartFrame = nowFrame > (uint)(cachedLapFrames * progress)
                ? nowFrame - (uint)math.round(cachedLapFrames * progress)
                : 0u;
            m_VehicleLapStartFrame[v] = estimatedStartFrame;
            m_VehicleLapFrames[v] = (uint)cachedLapFrames;

            if (EntityManager.HasComponent<Odometer>(v))
            {
                float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
                float cachedLapDistance = ReadLineLapDistance(line);
                if (cachedLapDistance > 0f)
                    m_VehicleLapStartOdometer[v] = currentOdo - cachedLapDistance * progress;
                else
                    m_VehicleLapStartOdometer[v] = currentOdo;
            }

            log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                + " Running进度恢复"
                + " progress=" + progress.ToString("F2")
                + " wp=" + nextWaypointIndex
                + " seg=" + segmentPosition.ToString("F2")
                + " lapFrames=" + ((uint)cachedLapFrames).ToString()
                + " startFrame=" + estimatedStartFrame
                + " from=" + initReason);
            return true;
        }
    }
}
