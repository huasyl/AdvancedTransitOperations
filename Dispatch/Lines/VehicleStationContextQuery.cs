using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using LineWaypointIndexLookup = RapidTransitMod.TrackModel.LineWaypointIndexLookup;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class ResolvedStation
    {
        public Entity StopEntity;
        public Entity AnchorEntity;
        public int WaypointIndex;
        public int Order;
        public string StationId = string.Empty;
        public string LegacyStationId = string.Empty;
        public string Name = string.Empty;
    }

    internal sealed class StationLineCache
    {
        public ulong Signature;
        public ResolvedStation[] Stations = Array.Empty<ResolvedStation>();
        public ResolvedStation[] StationByWaypoint = Array.Empty<ResolvedStation>();
        public Dictionary<string, ResolvedStation> StationById =
            new Dictionary<string, ResolvedStation>(StringComparer.Ordinal);
        public int[] StationOrderByWaypoint = Array.Empty<int>();
        public int[] NormalizedStationWaypointByWaypoint = Array.Empty<int>();
        public int[] NextDistinctStationWaypointByWaypoint = Array.Empty<int>();
        public int TerminalStationWaypointIndex = -1;
        public ResolvedStation[] TurnbackStations = Array.Empty<ResolvedStation>();
    }

    internal readonly struct VehicleStationContext
    {
        public readonly string LineId;
        public readonly Entity CurrentStopEntity;
        public readonly int CurrentStopWaypointIndex;
        public readonly string CurrentStationId;
        public readonly string CurrentStationName;
        public readonly int NextStopWaypointIndex;
        public readonly string NextStopStationId;
        public readonly string NextStopStationName;
        public readonly int NextPhysicalWaypointIndex;
        public readonly string NextPhysicalStationId;
        public readonly string NextPhysicalStationName;
        public readonly bool NextPhysicalIsPass;
        public readonly string TerminalStationId;
        public readonly string TerminalStationName;
        public readonly string TurnbackStationId;
        public readonly string TurnbackStationName;

        public VehicleStationContext(
            string lineId,
            Entity currentStopEntity,
            int currentStopWaypointIndex,
            string currentStationId,
            string currentStationName,
            int nextStopWaypointIndex,
            string nextStopStationId,
            string nextStopStationName,
            int nextPhysicalWaypointIndex,
            string nextPhysicalStationId,
            string nextPhysicalStationName,
            bool nextPhysicalIsPass,
            string terminalStationId,
            string terminalStationName,
            string turnbackStationId,
            string turnbackStationName)
        {
            LineId = lineId ?? string.Empty;
            CurrentStopEntity = currentStopEntity;
            CurrentStopWaypointIndex = currentStopWaypointIndex;
            CurrentStationId = currentStationId ?? string.Empty;
            CurrentStationName = currentStationName ?? string.Empty;
            NextStopWaypointIndex = nextStopWaypointIndex;
            NextStopStationId = nextStopStationId ?? string.Empty;
            NextStopStationName = nextStopStationName ?? string.Empty;
            NextPhysicalWaypointIndex = nextPhysicalWaypointIndex;
            NextPhysicalStationId = nextPhysicalStationId ?? string.Empty;
            NextPhysicalStationName = nextPhysicalStationName ?? string.Empty;
            NextPhysicalIsPass = nextPhysicalIsPass;
            TerminalStationId = terminalStationId ?? string.Empty;
            TerminalStationName = terminalStationName ?? string.Empty;
            TurnbackStationId = turnbackStationId ?? string.Empty;
            TurnbackStationName = turnbackStationName ?? string.Empty;
        }
    }

    internal sealed class VehicleStationContextQuery
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, Entity> m_Anchor;
        private readonly Func<Entity, Entity> m_AnchorFromStop;
        private readonly Func<Entity, string> m_EnsureSak;
        private readonly Func<Entity, string> m_Sak;
        private readonly Func<int, string> m_StationId;
        private readonly Func<Entity, string> m_StationName;
        private readonly Func<Entity, string> m_Name;
        private readonly Func<DynamicBuffer<RouteWaypoint>, ulong> m_Signature;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, LineWaypointIndexLookup> m_TryLookupUnsafe;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, LineTrackChain> m_TryChainUnsafe;
        private readonly Func<Entity, Entity, DynamicBuffer<RouteWaypoint>, LineTrackChain, VehicleTrackCursor?> m_TryCursorUnsafe;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<string, string> m_DraftKey;
        private readonly NativeHashMap<Entity, int> m_CachedWaypointIndex;
        private readonly Dictionary<Entity, int> m_CurrentStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_NextStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, string> m_CurrentStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_NextStopStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, ResolvedStation> m_NextPhysicalStationByVehicle = new Dictionary<Entity, ResolvedStation>();
        private readonly Dictionary<Entity, bool> m_NextPhysicalIsPassByVehicle = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, StationLineCache> m_LineCaches = new Dictionary<Entity, StationLineCache>();

        internal VehicleStationContextQuery(
            EntityManager entityManager,
            Func<Entity, Entity> stop,
            Func<Entity, Entity> anchor,
            Func<Entity, Entity> anchorFromStop,
            Func<Entity, string> ensureSak,
            Func<Entity, string> sak,
            Func<int, string> stationId,
            Func<Entity, string> stationName,
            Func<Entity, string> name,
            Func<DynamicBuffer<RouteWaypoint>, ulong> signature,
            Func<Entity, DynamicBuffer<RouteWaypoint>, LineWaypointIndexLookup> tryLookupUnsafe,
            Func<Entity, DynamicBuffer<RouteWaypoint>, LineTrackChain> tryChainUnsafe,
            Func<Entity, Entity, DynamicBuffer<RouteWaypoint>, LineTrackChain, VehicleTrackCursor?> tryCursorUnsafe,
            Func<Entity, string> lineId,
            Func<string, string> draftKey,
            NativeHashMap<Entity, int> cachedWaypointIndex)
        {
            m_EntityManager = entityManager;
            m_Stop = stop;
            m_Anchor = anchor;
            m_AnchorFromStop = anchorFromStop;
            m_EnsureSak = ensureSak;
            m_Sak = sak;
            m_StationId = stationId;
            m_StationName = stationName;
            m_Name = name;
            m_Signature = signature;
            m_TryLookupUnsafe = tryLookupUnsafe;
            m_TryChainUnsafe = tryChainUnsafe;
            m_TryCursorUnsafe = tryCursorUnsafe;
            m_LineId = lineId;
            m_DraftKey = draftKey;
            m_CachedWaypointIndex = cachedWaypointIndex;
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            m_CurrentStopWaypointIndexByVehicle.Remove(vehicle);
            m_NextStopWaypointIndexByVehicle.Remove(vehicle);
            m_CurrentStationNameByVehicle.Remove(vehicle);
            m_NextStopStationNameByVehicle.Remove(vehicle);
            m_NextPhysicalStationByVehicle.Remove(vehicle);
            m_NextPhysicalIsPassByVehicle.Remove(vehicle);
        }

        internal void Clear()
        {
            m_CurrentStopWaypointIndexByVehicle.Clear();
            m_NextStopWaypointIndexByVehicle.Clear();
            m_CurrentStationNameByVehicle.Clear();
            m_NextStopStationNameByVehicle.Clear();
            m_NextPhysicalStationByVehicle.Clear();
            m_NextPhysicalIsPassByVehicle.Clear();
            m_LineCaches.Clear();
        }

        internal bool TryStation(Entity line, DynamicBuffer<RouteWaypoint> waypoints, string stationId, out ResolvedStation station)
        {
            station = null;
            if (line == Entity.Null
                || string.IsNullOrWhiteSpace(stationId)
                || !TryCache(line, waypoints, out StationLineCache cache))
            {
                return false;
            }

            if (cache.StationById != null
                && cache.StationById.TryGetValue(stationId, out ResolvedStation cachedStation))
            {
                station = cachedStation;
            }
            else
            {
                station = cache.Stations
                    .FirstOrDefault(candidate => candidate != null && string.Equals(candidate.StationId, stationId, StringComparison.Ordinal));
            }

            return station != null;
        }

        internal string NormalizeRepresentativeStationId(Entity line, DynamicBuffer<RouteWaypoint> waypoints, string stationId)
        {
            return stationId ?? string.Empty;
        }

        internal bool TryCache(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out StationLineCache cache)
        {
            cache = null;
            if (line == Entity.Null
                || !m_EntityManager.Exists(line)
                || waypoints.Length == 0)
            {
                return false;
            }

            ulong signature = m_Signature(waypoints);
            if (m_LineCaches.TryGetValue(line, out cache)
                && cache != null
                && cache.Signature == signature)
            {
                return cache.Stations.Length > 0;
            }

            int waypointCount = waypoints.Length;
            int[] stationOrderByWaypoint = new int[waypointCount];
            int[] normalizedStationWaypointByWaypoint = new int[waypointCount];
            int[] nextDistinctStationWaypointByWaypoint = new int[waypointCount];
            ResolvedStation[] stationByWaypoint = new ResolvedStation[waypointCount];
            for (int i = 0; i < waypointCount; i++)
            {
                stationOrderByWaypoint[i] = -1;
                normalizedStationWaypointByWaypoint[i] = -1;
                nextDistinctStationWaypointByWaypoint[i] = -1;
            }

            List<ResolvedStation> stations = new List<ResolvedStation>();
            Dictionary<Entity, int> stationOrderByIdentity = new Dictionary<Entity, int>();
            for (int waypointIndex = 0; waypointIndex < waypointCount; waypointIndex++)
            {
                Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                Entity stopEntity = m_Stop(waypoint);
                Entity anchorEntity = m_Anchor(waypoint);
                Entity identityEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity;
                if (anchorEntity == Entity.Null || identityEntity == Entity.Null)
                    continue;

                if (!stationOrderByIdentity.TryGetValue(identityEntity, out int order))
                {
                    order = stations.Count;
                    stationOrderByIdentity[identityEntity] = order;
                    string resolvedStationId = m_EnsureSak(anchorEntity);
                    stations.Add(new ResolvedStation
                    {
                        StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                        AnchorEntity = anchorEntity,
                        WaypointIndex = waypointIndex,
                        Order = order,
                        StationId = resolvedStationId,
                        LegacyStationId = m_StationId(order),
                        Name = stopEntity != Entity.Null
                            ? m_StationName(stopEntity)
                            : m_Name(anchorEntity)
                    });
                }

                stationOrderByWaypoint[waypointIndex] = order;
                normalizedStationWaypointByWaypoint[waypointIndex] = stations[order].WaypointIndex;
                stationByWaypoint[waypointIndex] = new ResolvedStation
                {
                    StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                    AnchorEntity = anchorEntity,
                    WaypointIndex = waypointIndex,
                    Order = order,
                    StationId = stations[order].StationId,
                    LegacyStationId = stations[order].LegacyStationId,
                    Name = stations[order].Name
                };
            }

            ResolvedStation[] stationArray = stations.ToArray();
            Dictionary<string, ResolvedStation> stationById = new Dictionary<string, ResolvedStation>(StringComparer.Ordinal);
            for (int i = 0; i < stationArray.Length; i++)
            {
                ResolvedStation station = stationArray[i];
                if (station != null
                    && !string.IsNullOrWhiteSpace(station.StationId)
                    && !stationById.ContainsKey(station.StationId))
                {
                    stationById[station.StationId] = station;
                }
            }

            for (int waypointIndex = 0; waypointIndex < waypointCount; waypointIndex++)
            {
                for (int offset = 0; offset < waypointCount; offset++)
                {
                    int candidateWaypointIndex = (waypointIndex + offset) % waypointCount;
                    if (stationByWaypoint[candidateWaypointIndex] == null)
                        continue;

                    nextDistinctStationWaypointByWaypoint[waypointIndex] = candidateWaypointIndex;
                    break;
                }
            }

            cache = new StationLineCache
            {
                Signature = signature,
                Stations = stationArray,
                StationByWaypoint = stationByWaypoint,
                StationOrderByWaypoint = stationOrderByWaypoint,
                NormalizedStationWaypointByWaypoint = normalizedStationWaypointByWaypoint,
                NextDistinctStationWaypointByWaypoint = nextDistinctStationWaypointByWaypoint,
                StationById = stationById,
                TerminalStationWaypointIndex = stationArray.Length > 0 ? stationArray[0].WaypointIndex : -1
            };
            cache.TurnbackStations = ResolveTurnbackStations(line, waypoints, cache);
            m_LineCaches[line] = cache;
            return stationArray.Length > 0;
        }

        internal bool TryResolve(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int preferredCurrentStopWaypointIndex,
            bool includePhysical,
            bool includePanelExtras,
            out VehicleStationContext context)
        {
            context = default;
            if (!TryCache(line, waypoints, out StationLineCache cache))
                return false;

            ResolvedStation currentStation = null;
            ResolvedStation nextStoppingStation = null;
            ResolvedStation nextPhysicalStation = null;
            bool nextPhysicalIsPass = false;
            ResolvedStation cachedCurrentStation = null;

            if (preferredCurrentStopWaypointIndex >= 0)
                TryStationByWaypoint(cache, preferredCurrentStopWaypointIndex, out currentStation);

            if (currentStation == null
                && m_CachedWaypointIndex.TryGetValue(vehicle, out int liveStopWaypointIndex)
                && liveStopWaypointIndex >= 0)
            {
                TryStationByWaypoint(cache, liveStopWaypointIndex, out currentStation);
            }

            if (m_CurrentStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedCurrentWaypointIndex))
                TryStationByWaypoint(cache, cachedCurrentWaypointIndex, out cachedCurrentStation);

            if (currentStation == null
                && TryVehicleTargetWaypoint(vehicle, line, waypoints, out int targetWaypointIndex)
                && TryNextStationAtOrAfterWaypoint(cache, targetWaypointIndex, out ResolvedStation targetNextStation))
            {
                if (cachedCurrentStation != null && targetNextStation.Order <= cachedCurrentStation.Order)
                {
                    currentStation = cachedCurrentStation;
                    if (targetNextStation.WaypointIndex != cachedCurrentStation.WaypointIndex)
                        nextStoppingStation = targetNextStation;
                }
                else if (TryPreviousStationBeforeWaypoint(cache, targetWaypointIndex, out ResolvedStation targetCurrentStation))
                {
                    currentStation = targetCurrentStation;
                    nextStoppingStation = targetNextStation;
                }
            }

            if (currentStation == null)
                currentStation = cachedCurrentStation;

            if (nextStoppingStation == null
                && m_NextStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedNextWaypointIndex))
            {
                TryStationByWaypoint(cache, cachedNextWaypointIndex, out nextStoppingStation);
            }

            if (currentStation != null
                && (nextStoppingStation == null || nextStoppingStation.WaypointIndex == currentStation.WaypointIndex))
            {
                TryNextStationAfterWaypoint(cache, currentStation.WaypointIndex, out nextStoppingStation);
            }

            if (currentStation == null)
                return false;

            if (includePhysical)
            {
                TryNextPhysicalStation(
                    vehicle,
                    line,
                    waypoints,
                    cache,
                    currentStation,
                    out nextPhysicalStation,
                    out nextPhysicalIsPass);
            }

            ResolvedStation terminalStation = includePanelExtras ? cache.Stations[0] : null;
            ResolvedStation turnbackStation = includePanelExtras
                && TryTurnback(vehicle, line, waypoints, cache, out ResolvedStation resolvedTurnbackStation)
                ? resolvedTurnbackStation
                : null;
            string lineId = includePanelExtras ? m_DraftKey(m_LineId(line)) : string.Empty;

            context = new VehicleStationContext(
                lineId,
                currentStation.StopEntity,
                currentStation.WaypointIndex,
                currentStation.StationId,
                currentStation.Name,
                nextStoppingStation?.WaypointIndex ?? -1,
                nextStoppingStation?.StationId ?? string.Empty,
                nextStoppingStation?.Name ?? string.Empty,
                nextPhysicalStation?.WaypointIndex ?? -1,
                nextPhysicalStation?.StationId ?? string.Empty,
                nextPhysicalStation?.Name ?? string.Empty,
                nextPhysicalIsPass,
                terminalStation?.StationId ?? string.Empty,
                terminalStation?.Name ?? string.Empty,
                turnbackStation?.StationId ?? string.Empty,
                turnbackStation?.Name ?? string.Empty);

            if (vehicle != Entity.Null)
            {
                m_CurrentStopWaypointIndexByVehicle[vehicle] = context.CurrentStopWaypointIndex;
                if (context.NextStopWaypointIndex >= 0)
                    m_NextStopWaypointIndexByVehicle[vehicle] = context.NextStopWaypointIndex;
                else
                    m_NextStopWaypointIndexByVehicle.Remove(vehicle);

                m_CurrentStationNameByVehicle[vehicle] = context.CurrentStationName;
                m_NextStopStationNameByVehicle[vehicle] = context.NextStopStationName;
                if (includePhysical && nextPhysicalStation != null)
                {
                    m_NextPhysicalStationByVehicle[vehicle] = nextPhysicalStation;
                    m_NextPhysicalIsPassByVehicle[vehicle] = nextPhysicalIsPass;
                }
                else if (includePhysical)
                {
                    m_NextPhysicalStationByVehicle.Remove(vehicle);
                    m_NextPhysicalIsPassByVehicle.Remove(vehicle);
                }
            }

            return true;
        }

        internal bool TryPanelContext(
            Entity vehicle,
            Entity line,
            out string currentStationName,
            out string nextStopStationName,
            out string nextPhysicalStationName,
            out bool nextPhysicalIsPass,
            out string terminalStationName)
        {
            currentStationName = m_CurrentStationNameByVehicle.TryGetValue(vehicle, out string currentName)
                ? currentName ?? string.Empty
                : string.Empty;
            nextStopStationName = m_NextStopStationNameByVehicle.TryGetValue(vehicle, out string nextName)
                ? nextName ?? string.Empty
                : string.Empty;
            nextPhysicalStationName = m_NextPhysicalStationByVehicle.TryGetValue(vehicle, out ResolvedStation cachedPhysical)
                ? cachedPhysical?.Name ?? string.Empty
                : string.Empty;
            nextPhysicalIsPass = m_NextPhysicalIsPassByVehicle.TryGetValue(vehicle, out bool cachedPhysicalIsPass)
                && cachedPhysicalIsPass;
            terminalStationName = string.Empty;

            if (line == Entity.Null || !m_EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return !string.IsNullOrEmpty(currentStationName)
                    || !string.IsNullOrEmpty(nextStopStationName)
                    || !string.IsNullOrEmpty(nextPhysicalStationName);
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (TryResolve(vehicle, line, waypoints, -1, true, true, out VehicleStationContext context))
            {
                currentStationName = context.CurrentStationName;
                nextStopStationName = context.NextStopStationName;
                nextPhysicalStationName = context.NextPhysicalStationName;
                nextPhysicalIsPass = context.NextPhysicalIsPass;
                terminalStationName = context.TerminalStationName;
            }

            return !string.IsNullOrEmpty(currentStationName)
                || !string.IsNullOrEmpty(nextStopStationName)
                || !string.IsNullOrEmpty(nextPhysicalStationName)
                || !string.IsNullOrEmpty(terminalStationName);
        }

        internal bool TryPanelStations(
            Entity vehicle,
            Entity line,
            int preferredCurrentStopWaypointIndex,
            bool includePhysical,
            out VehicleStationContext context)
        {
            context = default;
            if (line == Entity.Null || !m_EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            return TryResolve(
                vehicle,
                line,
                waypoints,
                preferredCurrentStopWaypointIndex,
                includePhysical,
                false,
                out context);
        }

        internal static ResolvedStation TurnbackAfterWaypoint(StationLineCache cache, ResolvedStation currentStation)
        {
            if (cache?.TurnbackStations == null || cache.TurnbackStations.Length == 0)
                return null;

            int waypointIndex = currentStation?.WaypointIndex ?? -1;
            ResolvedStation terminalStation = cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
            ResolvedStation firstStation = null;
            ResolvedStation nextStation = null;
            List<ResolvedStation> candidates = new List<ResolvedStation>();
            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                ResolvedStation station = cache.TurnbackStations[i];
                if (station != null)
                    candidates.Add(station);
            }

            if (terminalStation != null && !candidates.Any(candidate => IsSameStation(candidate, terminalStation)))
                candidates.Add(terminalStation);

            for (int i = 0; i < candidates.Count; i++)
            {
                ResolvedStation station = candidates[i];
                if (firstStation == null || station.WaypointIndex < firstStation.WaypointIndex)
                    firstStation = station;

                if (station.WaypointIndex > waypointIndex
                    && !IsSameStation(station, currentStation)
                    && (nextStation == null || station.WaypointIndex < nextStation.WaypointIndex))
                {
                    nextStation = station;
                }
            }

            if (nextStation != null)
                return nextStation;
            if (firstStation != null && !IsSameStation(firstStation, currentStation))
                return firstStation;

            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                ResolvedStation station = cache.TurnbackStations[i];
                if (!IsSameStation(station, currentStation))
                    return station;
            }

            return firstStation;
        }

        private bool TryNextPhysicalStation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            StationLineCache cache,
            ResolvedStation currentStation,
            out ResolvedStation nextPhysicalStation,
            out bool nextPhysicalIsPass)
        {
            if (TryNextPhysicalStationOnce(vehicle, line, waypoints, cache, currentStation, out nextPhysicalStation, out nextPhysicalIsPass))
            {
                CacheNextPhysicalStation(vehicle, nextPhysicalStation, nextPhysicalIsPass);
                return true;
            }

            return TryCachedNextPhysicalStation(vehicle, out nextPhysicalStation, out nextPhysicalIsPass);
        }

        private bool TryNextPhysicalStationOnce(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            StationLineCache cache,
            ResolvedStation currentStation,
            out ResolvedStation nextPhysicalStation,
            out bool nextPhysicalIsPass)
        {
            nextPhysicalStation = null;
            nextPhysicalIsPass = false;
            LineTrackChain chain = m_TryChainUnsafe(line, waypoints);
            if (chain == null || chain.TraversalProfile == null || chain.TraversalProfile.Events == null)
                return false;

            VehicleTrackCursor? cursor = m_TryCursorUnsafe(vehicle, line, waypoints, chain);
            int referenceAtomIndex = cursor?.AtomCursorIndex ?? -1;
            if (referenceAtomIndex < 0)
                return false;
            if (cursor.Value.Source != VehicleTrackCursorSource.CurrentLane)
                return false;

            if (!TraversalProfileQuery.TryGetNextPhysicalStationEvent(chain, referenceAtomIndex, out TraversalEvent traversalEvent))
                return false;

            ResolvedStation station = ResolveEventStation(cache, traversalEvent);
            bool isPass = !IsPhysicalStationWaypointForLine(cache, traversalEvent.WaypointIndex);
            if (station == null)
                return false;

            if (currentStation != null
                && IsSameStation(station, currentStation)
                && referenceAtomIndex >= traversalEvent.StartAtomIndex)
            {
                int nextAtomIndex = math.max(referenceAtomIndex + 1, traversalEvent.EndAtomIndexExclusive);
                if (!TraversalProfileQuery.TryGetNextPhysicalStationEvent(chain, nextAtomIndex, out traversalEvent))
                    return false;

                station = ResolveEventStation(cache, traversalEvent);
                if (station == null)
                    return false;
                if (IsSameStation(station, currentStation))
                    return false;

                isPass = !IsPhysicalStationWaypointForLine(cache, traversalEvent.WaypointIndex);
            }

            nextPhysicalStation = station;
            nextPhysicalIsPass = isPass;
            return true;
        }

        private void CacheNextPhysicalStation(Entity vehicle, ResolvedStation station, bool isPass)
        {
            if (vehicle == Entity.Null || station == null)
                return;

            m_NextPhysicalStationByVehicle[vehicle] = station;
            m_NextPhysicalIsPassByVehicle[vehicle] = isPass;
        }

        private bool TryCachedNextPhysicalStation(Entity vehicle, out ResolvedStation station, out bool isPass)
        {
            station = null;
            isPass = false;
            if (vehicle == Entity.Null
                || !m_NextPhysicalStationByVehicle.TryGetValue(vehicle, out station)
                || station == null)
            {
                return false;
            }

            isPass = m_NextPhysicalIsPassByVehicle.TryGetValue(vehicle, out bool cachedIsPass)
                && cachedIsPass;
            return true;
        }

        private static bool IsPhysicalStationWaypointForLine(StationLineCache cache, int waypointIndex)
        {
            if (cache == null)
                return false;

            return waypointIndex >= 0 && TryStationByWaypoint(cache, waypointIndex, out _);
        }

        private ResolvedStation ResolveEventStation(StationLineCache cache, TraversalEvent traversalEvent)
        {
            if (cache == null)
                return null;

            if (traversalEvent.WaypointIndex >= 0
                && TryStationByWaypoint(cache, traversalEvent.WaypointIndex, out ResolvedStation waypointStation))
            {
                return waypointStation;
            }

            return ResolveTransientPhysicalStation(traversalEvent);
        }

        private ResolvedStation ResolveTransientPhysicalStation(TraversalEvent traversalEvent)
        {
            Entity building = traversalEvent.Building;
            if (building == Entity.Null)
                return null;

            if (m_EntityManager.HasComponent<TransportStop>(building))
            {
                Entity anchorEntity = m_AnchorFromStop(building);
                if (anchorEntity == Entity.Null)
                    anchorEntity = building;

                string stopStationId = m_Sak(anchorEntity);
                if (string.IsNullOrWhiteSpace(stopStationId))
                    stopStationId = m_EnsureSak(anchorEntity);

                string stopStationName = m_StationName(building);

                return new ResolvedStation
                {
                    StopEntity = building,
                    AnchorEntity = anchorEntity,
                    WaypointIndex = -1,
                    Order = -building.Index - 1,
                    StationId = stopStationId,
                    LegacyStationId = stopStationId,
                    Name = stopStationName
                };
            }

            string stationId = "physical:" + building.Index + ":" + traversalEvent.PassIndex;

            string stationName = m_Name(building);
            if (string.IsNullOrWhiteSpace(stationName))
                stationName = "#" + building.Index;

            return new ResolvedStation
            {
                StopEntity = building,
                AnchorEntity = building,
                WaypointIndex = -1,
                Order = -building.Index - 1,
                StationId = stationId,
                LegacyStationId = stationId,
                Name = stationName
            };
        }

        private ResolvedStation[] ResolveTurnbackStations(Entity line, DynamicBuffer<RouteWaypoint> waypoints, StationLineCache cache)
        {
            if (line == Entity.Null
                || waypoints.Length == 0
                || cache == null)
                return Array.Empty<ResolvedStation>();

            LineTrackChain chain = m_TryChainUnsafe(line, waypoints);
            if (chain == null)
                return Array.Empty<ResolvedStation>();

            List<TrackTurnbackStationBoundary> stationBoundaries = new List<TrackTurnbackStationBoundary>();
            if (!Turnbacks.TryCollectTurnbackStationBoundaries(chain, stationBoundaries)
                || stationBoundaries.Count == 0)
            {
                return Array.Empty<ResolvedStation>();
            }

            List<ResolvedStation> stations = new List<ResolvedStation>();
            for (int i = 0; i < stationBoundaries.Count; i++)
            {
                TryTurnbackFromBoundary(cache, stationBoundaries[i], out ResolvedStation station);
                stations.Add(station);
            }

            return stations.ToArray();
        }

        private bool TryTurnback(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            StationLineCache cache,
            out ResolvedStation station)
        {
            station = null;
            if (cache == null || line == Entity.Null || waypoints.Length == 0)
                return false;

            LineTrackChain chain = m_TryChainUnsafe(line, waypoints);
            if (chain == null)
                return false;

            int atomCursorIndex = -1;
            VehicleTrackCursor? cursor = m_TryCursorUnsafe(vehicle, line, waypoints, chain);
            if (cursor.HasValue)
                atomCursorIndex = cursor.Value.AtomCursorIndex;

            ResolvedStation terminalStation = cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
            if (!TryTurnbackBoundaryWithWrap(chain, atomCursorIndex, out TrackTurnbackStationBoundary stationBoundary))
                return false;

            bool resolved = TryTurnbackFromBoundary(cache, stationBoundary, out station);
            bool wrappedToStart = atomCursorIndex >= 0 && stationBoundary.AtomIndex <= atomCursorIndex;
            if (wrappedToStart && resolved && terminalStation != null && !IsSameStation(station, terminalStation))
            {
                station = terminalStation;
                return true;
            }

            return resolved;
        }

        private bool TryTurnbackBoundaryWithWrap(LineTrackChain chain, int atomCursorIndex, out TrackTurnbackStationBoundary stationBoundary)
        {
            stationBoundary = default;
            if (chain == null || chain.TurnbackBoundaries == null || chain.TurnbackBoundaries.Count == 0)
                return false;

            int cursorIndex = atomCursorIndex >= 0 ? atomCursorIndex : -1;
            bool hasFirstResolvedBoundary = false;
            TrackTurnbackStationBoundary firstResolvedBoundary = default;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[boundaryIndex];
                if (!Turnbacks.TryResolveTurnbackStationBoundary(
                        chain,
                        boundary,
                        out TrackTurnbackStationBoundary candidate))
                {
                    continue;
                }

                if (!hasFirstResolvedBoundary)
                {
                    firstResolvedBoundary = candidate;
                    hasFirstResolvedBoundary = true;
                }

                if (cursorIndex < 0 || boundary.AtomIndex > cursorIndex)
                {
                    stationBoundary = candidate;
                    return true;
                }
            }

            if (hasFirstResolvedBoundary)
            {
                stationBoundary = firstResolvedBoundary;
                return true;
            }

            return false;
        }

        private bool TryTurnbackFromBoundary(StationLineCache cache, TrackTurnbackStationBoundary stationBoundary, out ResolvedStation station)
        {
            station = null;
            if (cache == null)
                return false;

            if (TryStationByWaypoint(cache, stationBoundary.WaypointIndex, out station))
                return true;

            if (stationBoundary.StationEntity == Entity.Null || cache.Stations == null)
                return false;

            Entity anchorEntity = m_AnchorFromStop(stationBoundary.StationEntity);
            string stationId = m_Sak(anchorEntity);
            if (!string.IsNullOrWhiteSpace(stationId)
                && cache.StationById != null
                && cache.StationById.TryGetValue(stationId, out station))
            {
                return true;
            }

            for (int i = 0; i < cache.Stations.Length; i++)
            {
                ResolvedStation candidate = cache.Stations[i];
                if (candidate != null && candidate.AnchorEntity == anchorEntity)
                {
                    station = candidate;
                    return true;
                }
            }

            station = null;
            return false;
        }

        private bool TryVehicleTargetWaypoint(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out int waypointIndex)
        {
            waypointIndex = -1;
            if (vehicle == Entity.Null || line == Entity.Null || !m_EntityManager.HasComponent<Target>(vehicle))
                return false;

            Entity target = m_EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null)
                return false;

            LineWaypointIndexLookup lookup = m_TryLookupUnsafe(line, waypoints);
            if (lookup != null)
            {
                if (lookup.WaypointIndexByWaypoint.TryGetValue(target, out waypointIndex))
                    return true;
                if (lookup.WaypointIndexByStop.TryGetValue(target, out waypointIndex))
                    return true;
            }

            if (!m_EntityManager.HasComponent<Waypoint>(target))
                return false;

            waypointIndex = m_EntityManager.GetComponentData<Waypoint>(target).m_Index;
            return waypointIndex >= 0 && waypointIndex < waypoints.Length;
        }

        private static bool TryStationByWaypoint(StationLineCache cache, int waypointIndex, out ResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.StationByWaypoint == null
                || waypointIndex < 0
                || waypointIndex >= cache.StationByWaypoint.Length)
            {
                return false;
            }

            station = cache.StationByWaypoint[waypointIndex];
            return station != null;
        }

        private static bool TryNextStationAtOrAfterWaypoint(StationLineCache cache, int waypointIndex, out ResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.NextDistinctStationWaypointByWaypoint == null
                || waypointIndex < 0
                || waypointIndex >= cache.NextDistinctStationWaypointByWaypoint.Length)
            {
                return false;
            }

            int stationWaypointIndex = cache.NextDistinctStationWaypointByWaypoint[waypointIndex];
            return TryStationByWaypoint(cache, stationWaypointIndex, out station);
        }

        private static bool TryNextStationAfterWaypoint(StationLineCache cache, int waypointIndex, out ResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.NextDistinctStationWaypointByWaypoint == null
                || cache.NextDistinctStationWaypointByWaypoint.Length == 0)
            {
                return false;
            }

            int nextWaypointIndex = (waypointIndex + 1) % cache.NextDistinctStationWaypointByWaypoint.Length;
            return TryNextStationAtOrAfterWaypoint(cache, nextWaypointIndex, out station);
        }

        private static bool TryPreviousStationBeforeWaypoint(StationLineCache cache, int waypointIndex, out ResolvedStation station)
        {
            station = null;
            if (cache == null || cache.StationByWaypoint == null || cache.StationByWaypoint.Length == 0)
                return false;

            int waypointCount = cache.StationByWaypoint.Length;
            waypointIndex = Mathf.Clamp(waypointIndex, 0, waypointCount - 1);
            for (int offset = 1; offset <= waypointCount; offset++)
            {
                int candidateWaypointIndex = (waypointIndex - offset + waypointCount) % waypointCount;
                station = cache.StationByWaypoint[candidateWaypointIndex];
                if (station != null)
                    return true;
            }

            station = null;
            return false;
        }

        private static bool IsSameStation(ResolvedStation left, ResolvedStation right)
        {
            if (left == null || right == null)
                return false;

            return left.Order == right.Order
                || left.StopEntity == right.StopEntity
                || string.Equals(left.StationId, right.StationId, StringComparison.Ordinal);
        }
    }
}
