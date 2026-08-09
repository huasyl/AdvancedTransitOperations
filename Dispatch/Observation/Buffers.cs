using Game.Common;
using Game.Pathfind;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.TrackModel;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Buffers
    {
        private const ulong SignatureSeed = 1469598103934665603UL;
        private readonly ModRuntimeHostSystem m_Runtime;

        public Buffers(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Ensure()
        {
            EnsureDwell();
            EnsureStationDwell();
            EnsureSlice();
            EnsureBusSeg();
        }

        public void EnsureDwell()
        {
            EnsureDwellCore();
        }

        public void EnsureStationDwell()
        {
            EnsureStationDwellCore();
        }

        public void EnsureSlice()
        {
            EnsureSliceCore();
        }

        public void EnsureBusSeg()
        {
            EnsureBusSegCore();
        }

        public void Load()
        {
            LoadDwell();
            LoadStationDwell();
            LoadSlice();
            LoadBusSeg();
        }

        public void LoadDwell()
        {
            RestoreDwellCore();
        }

        public void LoadStationDwell()
        {
            RestoreStationDwellCore();
        }

        public void LoadSlice()
        {
            RestoreSliceCore();
        }

        public void LoadBusSeg()
        {
            RestoreBusSegCore();
        }

        internal bool TrySliceSignature(Entity line, out ulong signature)
        {
            bool success = TrySliceSignatures(line, out signature, out _);
            return success;
        }

        public void Flush(Entity line, int index, DwellObservation observation)
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled())
                return;

            if (line == Entity.Null
                || index < 0
                || !(observation.AverageFrames > 0f)
                || observation.SampleCount <= 0
                || !m_Runtime.m_DwellObservationBufferReady)
            {
                return;
            }

            if (!TryGetSignature(line, out ulong profileSignature))
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                return;

            DynamicBuffer<DwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<DwellObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_WaypointIndex != index)
                    continue;

                buffer[i] = new DwellObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_WaypointIndex = index,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount
                };
                return;
            }

            buffer.Add(new DwellObservationElement
            {
                m_LineEntity = line,
                m_ProfileSignature = profileSignature,
                m_WaypointIndex = index,
                m_AverageFrames = observation.AverageFrames,
                m_SampleCount = observation.SampleCount
            });
        }

        public void Flush(string key, StationDwellObservation observation)
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled())
                return;

            if (string.IsNullOrWhiteSpace(key)
                || !Capture.IsStationDwellKey(key)
                || !(observation.AverageFrames > 0f)
                || observation.SampleCount <= 0
                || !m_Runtime.m_StationDwellObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                return;

            DynamicBuffer<StationDwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<StationDwellObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (!string.Equals(buffer[i].m_StationAnchorId.ToString(), key, System.StringComparison.Ordinal))
                    continue;

                buffer[i] = new StationDwellObservationElement
                {
                    m_StationAnchorId = key,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                };
                return;
            }

            buffer.Add(new StationDwellObservationElement
            {
                m_StationAnchorId = key,
                m_AverageFrames = observation.AverageFrames,
                m_SampleCount = observation.SampleCount,
                m_LastObservedFrame = observation.LastObservedFrame
            });
        }

        public void Flush(Entity line, int index, TraversalSliceObservation observation)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (line == Entity.Null || index < 0 || observation.SampleCount <= 0)
                return;

            if (!TrySliceSignatures(line, out ulong profileSignature, out _))
                return;

            if (!m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_SliceIndex != index)
                    continue;

                buffer[i] = new TraversalSliceObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_SliceIndex = index,
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
                m_SliceIndex = index,
                m_AverageFrames = observation.AverageFrames,
                m_FastBaselineFrames = observation.FastBaselineFrames,
                m_SampleCount = observation.SampleCount,
                m_LastObservedFrame = observation.LastObservedFrame
            });
        }

        public void SyncBusSeg(Entity line)
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || line == Entity.Null
                || !m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
            {
                return;
            }

            DynamicBuffer<BusSegObservationElement> observations =
                m_Runtime.EntityManager.GetBuffer<BusSegObservationElement>(city);
            for (int i = observations.Length - 1; i >= 0; i--)
            {
                if (observations[i].m_LineEntity == line)
                    observations.RemoveAt(i);
            }

            if (IsBusLine(line))
            {
                foreach (KeyValuePair<BusSegKey, BusSegObservation> pair in m_Runtime.m_ObsQuery.BusSegs)
                {
                    BusSegKey key = pair.Key;
                    BusSegObservation observation = pair.Value;
                    if (key.Line != line || !ValidBusObservation(key, observation))
                        continue;

                    observations.Add(new BusSegObservationElement
                    {
                        m_LineEntity = key.Line,
                        m_FromWaypointEntity = key.FromWaypoint,
                        m_FromStopEntity = key.FromStop,
                        m_ToWaypointEntity = key.ToWaypoint,
                        m_ToStopEntity = key.ToStop,
                        m_EstimatedFrames = observation.EstimatedFrames,
                        m_SampleCount = observation.SampleCount
                    });
                }
            }

            DynamicBuffer<BusRouteSnapshotElement> routes =
                m_Runtime.EntityManager.GetBuffer<BusRouteSnapshotElement>(city);
            for (int i = routes.Length - 1; i >= 0; i--)
            {
                if (routes[i].m_LineEntity == line)
                    routes.RemoveAt(i);
            }

            if (!TryBusRoute(line, out LineProfile.RoadRouteSnapshot snapshot))
                return;

            for (int i = 0; i < snapshot.Waypoints.Length; i++)
            {
                routes.Add(new BusRouteSnapshotElement
                {
                    m_LineEntity = line,
                    m_Order = i,
                    m_WaypointEntity = snapshot.Waypoints[i],
                    m_ResolvedStopEntity = snapshot.Stops[i]
                });
            }
        }

        public void RemoveSliceLine(Entity line)
        {
            if (line == Entity.Null || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i].m_LineEntity == line)
                    buffer.RemoveAt(i);
            }
        }

        internal bool TryFlushDailyQuota(LineKey lak, TraversalSliceDailyQuota quota)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled()
                || lak.IsEmpty
                || !m_Runtime.m_TraversalSliceObservationBufferReady)
            {
                return false;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
                return false;

            DynamicBuffer<TraversalSliceQuotaElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceQuotaElement>(city);
            string lineKey = lak.ToString();
            int found = -1;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }
            for (int i = buffer.Length - 1; i > found; i--)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
            }

            TraversalSliceQuotaElement entry = new TraversalSliceQuotaElement
            {
                m_Version = 1,
                m_LineKey = lineKey,
                m_DateKey = quota.DateKey,
                m_UsedCount = quota.UsedCount
            };
            if (found >= 0)
                buffer[found] = entry;
            else
                buffer.Add(entry);
            return true;
        }

        internal bool TryFlushColdStart(LineKey lak, TraversalSliceColdStart coldStart)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled()
                || lak.IsEmpty
                || !m_Runtime.m_TraversalSliceObservationBufferReady)
            {
                return false;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                return false;

            DynamicBuffer<TraversalSliceColdStartElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city);
            string lineKey = lak.ToString();
            int found = -1;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }
            for (int i = buffer.Length - 1; i > found; i--)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
            }

            TraversalSliceColdStartElement entry = new TraversalSliceColdStartElement
            {
                m_Version = 2,
                m_LineKey = lineKey,
                m_ProfileSignature = coldStart.ProfileSignature,
                m_Remaining = coldStart.Remaining,
                m_PendingFinalMinute = coldStart.PendingFinalMinute,
                m_PendingFinalDateKey = coldStart.PendingFinalDateKey
            };
            if (found >= 0)
                buffer[found] = entry;
            else
                buffer.Add(entry);
            return true;
        }

        internal void RemoveColdStart(LineKey lak)
        {
            if (lak.IsEmpty || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                return;

            DynamicBuffer<TraversalSliceColdStartElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city);
            string lineKey = lak.ToString();
            for (int i = buffer.Length - 1; i >= 0; i--)
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
        }

        public bool TryWaypointPosition(Entity waypoint, out float3 position)
        {
            return m_Runtime.m_MileageStore.TryWaypointPosition(waypoint, out position);
        }

        private void EnsureDwellCore()
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled() || m_Runtime.m_DwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<DwellObservationElement>(city);

            m_Runtime.m_DwellObservationBufferReady = true;
        }

        private void RestoreDwellCore()
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_DwellObservationCacheLoaded || !m_Runtime.m_DwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearWaypointDwell();
            DynamicBuffer<DwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<DwellObservationElement>(city, true);
            int restoredCount = 0;
            int restoredByLegacyTopologyCount = 0;
            int skippedSignatureMismatchCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                DwellObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null
                    || entry.m_WaypointIndex < 0
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    continue;
                }

                bool signatureMatched = TryGetSignature(entry.m_LineEntity, out ulong currentSignature)
                    && currentSignature == entry.m_ProfileSignature;
                if (!signatureMatched && !CanRestoreLegacy(entry.m_LineEntity, entry.m_WaypointIndex))
                {
                    skippedSignatureMismatchCount++;
                    continue;
                }
                if (!signatureMatched)
                    restoredByLegacyTopologyCount++;

                m_Runtime.m_ObsPersist.PutWaypointDwell(
                    Keys.WaypointDwell(entry.m_LineEntity, entry.m_WaypointIndex),
                    new DwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount)
                    });
                restoredCount++;
            }

            m_Runtime.m_DwellObservationCacheLoaded = true;
            m_Runtime.m_LastStationStopDwellLegacyBufferCount = buffer.Length;
            m_Runtime.m_LastStationStopDwellLegacyRestoredCount = restoredCount;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] DwellObservations buffer=" + buffer.Length
                    + " restored=" + restoredCount
                    + " legacyTopologyFallback=" + restoredByLegacyTopologyCount
                    + " skippedSignatureMismatch=" + skippedSignatureMismatchCount);
            }
        }

        private void EnsureStationDwellCore()
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled() || m_Runtime.m_StationDwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<StationDwellObservationElement>(city);

            m_Runtime.m_StationDwellObservationBufferReady = true;
        }

        private void RestoreStationDwellCore()
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_StationDwellObservationCacheLoaded || !m_Runtime.m_StationDwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearStationDwell();
            DynamicBuffer<StationDwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<StationDwellObservationElement>(city, true);
            int restoredCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                StationDwellObservationElement entry = buffer[i];
                string observationKey = entry.m_StationAnchorId.ToString();
                if (string.IsNullOrWhiteSpace(observationKey)
                    || !Capture.IsStationDwellKey(observationKey)
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    continue;
                }

                m_Runtime.m_ObsPersist.PutStationDwell(
                    observationKey,
                    new StationDwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount),
                        LastObservedFrame = entry.m_LastObservedFrame
                    });
                restoredCount++;
            }

            m_Runtime.m_StationDwellObservationCacheLoaded = true;
            m_Runtime.m_LastStationStopDwellAnchorBufferCount = buffer.Length;
            m_Runtime.m_LastStationStopDwellAnchorRestoredCount = restoredCount;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[StopDwellAnchorRestore] anchorBuffer=" + buffer.Length
                    + " anchorRestored=" + restoredCount
                    + " legacyBuffer=" + m_Runtime.m_LastStationStopDwellLegacyBufferCount
                    + " legacyRestored=" + m_Runtime.m_LastStationStopDwellLegacyRestoredCount
                    + " legacyPreserved=1");
            }
        }

        private void EnsureSliceCore()
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled() || m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceObservationElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceQuotaElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceColdStartElement>(city);

            m_Runtime.m_TraversalSliceObservationBufferReady = true;
        }

        private void EnsureBusSegCore()
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<BusSegObservationElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
                m_Runtime.EntityManager.AddBuffer<BusRouteSnapshotElement>(city);

            m_Runtime.m_BusSegObservationBufferReady = true;
        }

        private void RestoreSliceCore()
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_TraversalSliceObservationCacheLoaded || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearSliceObservations();
            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            int storedCount = buffer.Length;
            int restoredCount = 0;
            int legacyRestoredCount = 0;
            int removedMismatchCount = 0;
            int unavailableCount = 0;
            int removedDuplicateCount = 0;
            int removedInvalidCount = 0;
            Dictionary<Entity, ulong> geometrySignatures = new Dictionary<Entity, ulong>();
            Dictionary<Entity, ulong> legacySignatures = new Dictionary<Entity, ulong>();
            HashSet<Entity> unavailableLines = new HashSet<Entity>();
            Dictionary<ulong, int> winners = new Dictionary<ulong, int>();
            for (int i = 0; i < buffer.Length; i++)
            {
                TraversalSliceObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_SliceIndex < 0)
                    continue;

                if (!geometrySignatures.ContainsKey(entry.m_LineEntity)
                    && !unavailableLines.Contains(entry.m_LineEntity))
                {
                    if (TrySliceSignatures(entry.m_LineEntity, out ulong geometrySignature, out ulong legacySignature))
                    {
                        geometrySignatures[entry.m_LineEntity] = geometrySignature;
                        legacySignatures[entry.m_LineEntity] = legacySignature;
                    }
                    else
                    {
                        unavailableLines.Add(entry.m_LineEntity);
                    }
                }

                ulong key = Keys.Slice(entry.m_LineEntity, entry.m_SliceIndex);
                if (!winners.TryGetValue(key, out int winner)
                    || IsBetterSlice(
                        entry,
                        buffer[winner],
                        unavailableLines.Contains(entry.m_LineEntity),
                        geometrySignatures.TryGetValue(entry.m_LineEntity, out ulong geometry) ? geometry : 0UL,
                        legacySignatures.TryGetValue(entry.m_LineEntity, out ulong legacy) ? legacy : 0UL))
                    winners[key] = i;
            }

            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                TraversalSliceObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_SliceIndex < 0)
                {
                    buffer.RemoveAt(i);
                    removedInvalidCount++;
                    continue;
                }

                ulong key = Keys.Slice(entry.m_LineEntity, entry.m_SliceIndex);
                if (winners[key] != i)
                {
                    buffer.RemoveAt(i);
                    removedDuplicateCount++;
                    continue;
                }

                if (unavailableLines.Contains(entry.m_LineEntity))
                {
                    unavailableCount++;
                    continue;
                }

                ulong geometrySignature = geometrySignatures[entry.m_LineEntity];
                if (entry.m_ProfileSignature != geometrySignature)
                {
                    if (entry.m_ProfileSignature != legacySignatures[entry.m_LineEntity])
                    {
                        buffer.RemoveAt(i);
                        removedMismatchCount++;
                        continue;
                    }

                    entry.m_ProfileSignature = geometrySignature;
                    buffer[i] = entry;
                    legacyRestoredCount++;
                }

                m_Runtime.m_ObsPersist.PutSlice(
                    entry.m_LineEntity,
                    key,
                    new TraversalSliceObservation(
                        entry.m_AverageFrames,
                        entry.m_FastBaselineFrames > 0f ? entry.m_FastBaselineFrames : entry.m_AverageFrames,
                        math.max(0, entry.m_SampleCount),
                        entry.m_LastObservedFrame));
                restoredCount++;
            }

            m_Runtime.m_ObsPersist.ClearAdmissionState();
            if (m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
            {
                DynamicBuffer<TraversalSliceQuotaElement> quotas = m_Runtime.EntityManager.GetBuffer<TraversalSliceQuotaElement>(city, true);
                for (int i = 0; i < quotas.Length; i++)
                {
                    TraversalSliceQuotaElement entry = quotas[i];
                    if (entry.m_Version != 1
                        || !LineKey.TryParse(entry.m_LineKey.ToString(), out LineKey lak)
                        || !LineKey.IsStableGuidKey(lak))
                    {
                        continue;
                    }
                    m_Runtime.m_ObsPersist.PutDailyQuota(lak, entry.m_DateKey, math.clamp(entry.m_UsedCount, 0, 4));
                }
            }
            if (m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
            {
                DynamicBuffer<TraversalSliceColdStartElement> coldStarts = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city, true);
                for (int i = 0; i < coldStarts.Length; i++)
                {
                    TraversalSliceColdStartElement entry = coldStarts[i];
                    if ((entry.m_Version != 1 && entry.m_Version != 2)
                        || entry.m_Remaining < 0
                        || entry.m_Remaining > 3
                        || !LineKey.TryParse(entry.m_LineKey.ToString(), out LineKey lak)
                        || !LineKey.IsStableGuidKey(lak))
                    {
                        continue;
                    }
                    m_Runtime.m_ObsPersist.PutColdStart(
                        lak,
                        entry.m_ProfileSignature,
                        entry.m_Remaining,
                        entry.m_PendingFinalMinute,
                        entry.m_PendingFinalDateKey);
                }
            }

            m_Runtime.m_TraversalSliceObservationCacheLoaded = true;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] TraversalSliceObservations buffer=" + storedCount
                    + " restored=" + restoredCount
                    + " legacyRestored=" + legacyRestoredCount
                    + " removedMismatch=" + removedMismatchCount
                    + " unavailable=" + unavailableCount
                    + " removedDuplicate=" + removedDuplicateCount
                    + " removedInvalid=" + removedInvalidCount);
            }
        }

        private void RestoreBusSegCore()
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || m_Runtime.m_BusSegObservationCacheLoaded
                || !m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
            {
                return;
            }

            m_Runtime.m_ObsPersist.ClearBusSeg();
            DynamicBuffer<BusSegObservationElement> observations =
                m_Runtime.EntityManager.GetBuffer<BusSegObservationElement>(city, true);
            DynamicBuffer<BusRouteSnapshotElement> routeEntries =
                m_Runtime.EntityManager.GetBuffer<BusRouteSnapshotElement>(city, true);
            var savedRoutes = new Dictionary<Entity, List<BusRouteSnapshotElement>>();
            for (int i = 0; i < routeEntries.Length; i++)
            {
                BusRouteSnapshotElement entry = routeEntries[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_Order < 0)
                    continue;

                if (!savedRoutes.TryGetValue(entry.m_LineEntity, out List<BusRouteSnapshotElement> entries))
                {
                    entries = new List<BusRouteSnapshotElement>();
                    savedRoutes[entry.m_LineEntity] = entries;
                }
                entries.Add(entry);
            }

            var currentRoutes = new Dictionary<Entity, LineProfile.RoadRouteSnapshot>();
            var invalidLines = new HashSet<Entity>();
            int restored = 0;
            for (int i = 0; i < observations.Length; i++)
            {
                BusSegObservationElement entry = observations[i];
                BusSegKey key = new BusSegKey(
                    entry.m_LineEntity,
                    entry.m_FromWaypointEntity,
                    entry.m_FromStopEntity,
                    entry.m_ToWaypointEntity,
                    entry.m_ToStopEntity);
                BusSegObservation observation = new BusSegObservation(
                    entry.m_EstimatedFrames,
                    entry.m_SampleCount);
                if (!ValidBusObservation(key, observation)
                    || !savedRoutes.TryGetValue(key.Line, out List<BusRouteSnapshotElement> entries)
                    || !TrySnapshot(entries, out LineProfile.RoadRouteSnapshot saved))
                {
                    continue;
                }

                if (!currentRoutes.TryGetValue(key.Line, out LineProfile.RoadRouteSnapshot current))
                {
                    if (invalidLines.Contains(key.Line) || !TryBusRoute(key.Line, out current))
                    {
                        invalidLines.Add(key.Line);
                        continue;
                    }
                    currentRoutes[key.Line] = current;
                }

                if (!BusSegCapture.MatchesSegment(key, saved, current))
                    continue;

                m_Runtime.m_ObsPersist.PutBusSeg(key, observation);
                restored++;
            }

            m_Runtime.m_BusSegObservationCacheLoaded = true;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] BusSegObservations buffer=" + observations.Length
                    + " restored=" + restored);
            }
        }

        private bool IsBusLine(Entity line)
        {
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && TransportModeResolver.Resolve(m_Runtime.EntityManager, line) == TransitMode.Bus;
        }

        private bool ValidBusObservation(BusSegKey key, BusSegObservation observation)
        {
            return IsBusLine(key.Line)
                && key.FromWaypoint != Entity.Null
                && key.FromStop != Entity.Null
                && key.ToWaypoint != Entity.Null
                && key.ToStop != Entity.Null
                && m_Runtime.EntityManager.Exists(key.FromWaypoint)
                && m_Runtime.EntityManager.Exists(key.FromStop)
                && m_Runtime.EntityManager.Exists(key.ToWaypoint)
                && m_Runtime.EntityManager.Exists(key.ToStop)
                && math.isfinite(observation.EstimatedFrames)
                && observation.EstimatedFrames > 0f
                && observation.SampleCount > 0
                && observation.SampleCount <= 32;
        }

        private bool TryBusRoute(Entity line, out LineProfile.RoadRouteSnapshot snapshot)
        {
            snapshot = null;
            if (!IsBusLine(line) || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints =
                m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length < 2)
                return false;

            snapshot = new LineProfile.RoadRouteSnapshot
            {
                Waypoints = new Entity[waypoints.Length],
                Stops = new Entity[waypoints.Length]
            };
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null || !m_Runtime.EntityManager.Exists(waypoint))
                    return false;

                snapshot.Waypoints[i] = waypoint;
                snapshot.Stops[i] = m_Runtime.m_Resolve.Stop(waypoint);
            }

            return true;
        }

        private bool TrySnapshot(
            List<BusRouteSnapshotElement> entries,
            out LineProfile.RoadRouteSnapshot snapshot)
        {
            snapshot = null;
            if (entries == null || entries.Count < 2)
                return false;

            int last = -1;
            for (int i = 0; i < entries.Count; i++)
                last = math.max(last, entries[i].m_Order);
            if (last != entries.Count - 1)
                return false;

            var seen = new bool[entries.Count];
            snapshot = new LineProfile.RoadRouteSnapshot
            {
                Waypoints = new Entity[entries.Count],
                Stops = new Entity[entries.Count]
            };
            for (int i = 0; i < entries.Count; i++)
            {
                BusRouteSnapshotElement entry = entries[i];
                if (entry.m_Order < 0
                    || entry.m_Order >= entries.Count
                    || seen[entry.m_Order]
                    || entry.m_WaypointEntity == Entity.Null
                    || !m_Runtime.EntityManager.Exists(entry.m_WaypointEntity)
                    || (entry.m_ResolvedStopEntity != Entity.Null
                        && !m_Runtime.EntityManager.Exists(entry.m_ResolvedStopEntity)))
                {
                    snapshot = null;
                    return false;
                }

                seen[entry.m_Order] = true;
                snapshot.Waypoints[entry.m_Order] = entry.m_WaypointEntity;
                snapshot.Stops[entry.m_Order] = entry.m_ResolvedStopEntity;
            }

            return true;
        }

        private static bool IsBetterSlice(
            TraversalSliceObservationElement candidate,
            TraversalSliceObservationElement current,
            bool signatureUnavailable,
            ulong geometrySignature,
            ulong legacySignature)
        {
            int candidateRank = SliceSignatureRank(
                candidate.m_ProfileSignature,
                signatureUnavailable,
                geometrySignature,
                legacySignature);
            int currentRank = SliceSignatureRank(
                current.m_ProfileSignature,
                signatureUnavailable,
                geometrySignature,
                legacySignature);
            return candidateRank > currentRank
                || (candidateRank == currentRank
                    && (candidate.m_SampleCount > current.m_SampleCount
                || (candidate.m_SampleCount == current.m_SampleCount
                        && candidate.m_LastObservedFrame > current.m_LastObservedFrame)));
        }

        private static int SliceSignatureRank(
            ulong storedSignature,
            bool signatureUnavailable,
            ulong geometrySignature,
            ulong legacySignature)
        {
            if (signatureUnavailable)
                return 0;
            if (storedSignature == geometrySignature)
                return 2;
            return storedSignature == legacySignature ? 1 : 0;
        }

        private bool TrySliceSignatures(Entity line, out ulong geometry, out ulong legacyFull)
        {
            geometry = 0UL;
            legacyFull = 0UL;
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            BufferLookup<RouteSegment> segmentBuffers = m_Runtime.GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            geometry = SignatureSeed;
            legacyFull = SignatureSeed;
            geometry = m_Runtime.m_LineProfile.MixSignature(geometry, waypoints.Length);
            geometry = m_Runtime.m_LineProfile.MixSignature(geometry, segments.Length);
            legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, waypoints.Length);
            legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, segments.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, i);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, i);
                Entity waypointEntity = waypoints[i].m_Waypoint;
                int waypointIndex = -1;
                if (waypointEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypointEntity)
                    && m_Runtime.EntityManager.HasComponent<Waypoint>(waypointEntity))
                {
                    waypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(waypointEntity).m_Index;
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, waypointIndex);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, waypointIndex);

                int positionX = 0;
                int positionY = 0;
                int positionZ = 0;
                if (TryWaypointPosition(waypointEntity, out float3 waypointPosition))
                {
                    positionX = Quantize(waypointPosition.x);
                    positionY = Quantize(waypointPosition.y);
                    positionZ = Quantize(waypointPosition.z);
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionX);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionY);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionZ);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionX);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionY);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionZ);

                int startCurve = 0;
                int endCurve = 0;
                if (m_Runtime.EntityManager.HasComponent<RouteLane>(waypointEntity))
                {
                    RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(waypointEntity);
                    startCurve = (int)math.round(routeLane.m_StartCurvePos * 1000f);
                    endCurve = (int)math.round(routeLane.m_EndCurvePos * 1000f);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, routeLane.m_StartLane.Index);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, routeLane.m_EndLane.Index);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, startCurve);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, endCurve);
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, startCurve);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, endCurve);

                Entity segmentEntity = segments[i].m_Segment;
                float durationSeconds = 0f;
                if (segmentEntity == Entity.Null
                    || !m_Runtime.EntityManager.Exists(segmentEntity)
                    || !m_Runtime.EntityManager.HasComponent<PathInformation>(segmentEntity))
                {
                    durationSeconds = 0f;
                }
                else
                {
                    durationSeconds = math.max(0f, m_Runtime.EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Duration);
                }
                int distance = Quantize(m_Runtime.m_LineMileage.ReadSegment(segmentEntity, waypoints, i));
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, Quantize(durationSeconds));
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, distance);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, distance);
            }

            if (TransportModeResolver.Resolve(m_Runtime.EntityManager, line) != TransitMode.Tram)
                return geometry != 0UL && legacyFull != 0UL;

            if (m_Runtime.m_TrackModel == null
                || !m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || chain == null
                || chain.TraversalProfile == null)
            {
                geometry = 0UL;
                legacyFull = 0UL;
                return false;
            }

            MixTraversalEvents(chain, ref geometry);
            MixTraversalEvents(chain, ref legacyFull);

            return geometry != 0UL && legacyFull != 0UL;
        }

        private void MixTraversalEvents(LineTrackChain chain, ref ulong signature)
        {
            signature = m_Runtime.m_LineProfile.MixSignature(signature, chain.TraversalProfile.Events.Count);
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.EventIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, (int)traversalEvent.Kind);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    traversalEvent.Building == Entity.Null ? -1 : traversalEvent.Building.Index);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    traversalEvent.Building == Entity.Null ? -1 : traversalEvent.Building.Version);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.WaypointIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.PassIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.StartAtomIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.EndAtomIndexExclusive);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    (int)math.round(traversalEvent.StopFrames * 10f));
            }
        }

        private bool TryGetSignature(Entity line, out ulong signature)
        {
            signature = 0UL;
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line) || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            BufferLookup<RouteSegment> segmentBuffers = m_Runtime.GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            signature = ComputeSignature(waypoints, segments);
            return signature != 0UL;
        }

        private ulong ComputeSignature(DynamicBuffer<RouteWaypoint> waypoints, DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = SignatureSeed;
            hash = m_Runtime.m_LineProfile.MixSignature(hash, waypoints.Length);
            hash = m_Runtime.m_LineProfile.MixSignature(hash, segments.Length);
            int count = math.min(waypoints.Length, segments.Length);
            for (int i = 0; i < count; i++)
            {
                hash = m_Runtime.m_LineProfile.MixSignature(hash, i);

                Entity waypointEntity = waypoints[i].m_Waypoint;
                if (waypointEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypointEntity)
                    && m_Runtime.EntityManager.HasComponent<Waypoint>(waypointEntity))
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, m_Runtime.EntityManager.GetComponentData<Waypoint>(waypointEntity).m_Index);
                }
                else
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, -1);
                }

                if (TryWaypointPosition(waypointEntity, out float3 waypointPosition))
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.x));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.y));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.z));
                }
                else
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                }

                if (m_Runtime.EntityManager.HasComponent<RouteLane>(waypointEntity))
                {
                    RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(waypointEntity);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, routeLane.m_StartLane.Index);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, routeLane.m_EndLane.Index);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, (int)math.round(routeLane.m_StartCurvePos * 1000f));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, (int)math.round(routeLane.m_EndCurvePos * 1000f));
                }

                Entity segmentEntity = segments[i].m_Segment;
                float durationSeconds = 0f;
                if (segmentEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(segmentEntity)
                    && m_Runtime.EntityManager.HasComponent<PathInformation>(segmentEntity))
                {
                    durationSeconds = math.max(0f, m_Runtime.EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Duration);
                }

                hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(durationSeconds));
                hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(m_Runtime.m_LineMileage.ReadSegment(segmentEntity, waypoints, i)));
            }

            return hash;
        }

        private static int Quantize(float value)
        {
            if (!math.isfinite(value))
                return 0;

            return (int)math.round(value * 10f);
        }

        private bool CanRestoreLegacy(Entity line, int waypointIndex)
        {
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || waypointIndex < 0
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length)
                return false;

            Entity stopEntity = m_Runtime.m_Resolve.Stop(waypoints[waypointIndex].m_Waypoint);
            return stopEntity != Entity.Null && m_Runtime.EntityManager.Exists(stopEntity);
        }
    }
}
