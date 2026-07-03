using System;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using UnityEngine;
using LineWaypointIndexLookup = RapidTransitMod.TrackModel.LineWaypointIndexLookup;

namespace RapidTransitMod.Broadcasting
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

    internal readonly struct TriggerContext
    {
        public readonly string LineId;
        public readonly Entity CurrentStopEntity;
        public readonly string CurrentStationName;
        public readonly string NextStationName;
        public readonly string TerminalStationName;
        public readonly string TurnbackStationName;
        public readonly string CurrentStationAssetName;
        public readonly string NextStationAssetName;
        public readonly string TerminalStationAssetName;
        public readonly string TurnbackStationAssetName;
        public readonly List<BroadcastWorkbenchStationBindingDto> CurrentStationBindings;
        public readonly List<BroadcastWorkbenchStationBindingDto> NextStationBindings;
        public readonly List<BroadcastWorkbenchStationBindingDto> TerminalStationBindings;
        public readonly List<BroadcastWorkbenchStationBindingDto> TurnbackStationBindings;

        public TriggerContext(
            string lineId,
            Entity currentStopEntity,
            string currentStationName,
            string nextStationName,
            string terminalStationName,
            string turnbackStationName,
            string currentStationAssetName,
            string nextStationAssetName,
            string terminalStationAssetName,
            string turnbackStationAssetName,
            List<BroadcastWorkbenchStationBindingDto> currentStationBindings,
            List<BroadcastWorkbenchStationBindingDto> nextStationBindings,
            List<BroadcastWorkbenchStationBindingDto> terminalStationBindings,
            List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings)
        {
            LineId = lineId;
            CurrentStopEntity = currentStopEntity;
            CurrentStationName = currentStationName;
            NextStationName = nextStationName;
            TerminalStationName = terminalStationName;
            TurnbackStationName = turnbackStationName;
            CurrentStationAssetName = currentStationAssetName;
            NextStationAssetName = nextStationAssetName;
            TerminalStationAssetName = terminalStationAssetName;
            TurnbackStationAssetName = turnbackStationAssetName;
            CurrentStationBindings = currentStationBindings;
            NextStationBindings = nextStationBindings;
            TerminalStationBindings = terminalStationBindings;
            TurnbackStationBindings = turnbackStationBindings;
        }
    }

    internal readonly struct VehicleStation
    {
        public readonly string LineId;
        public readonly Entity CurrentStopEntity;
        public readonly int CurrentStopWaypointIndex;
        public readonly string CurrentStationId;
        public readonly string CurrentStationName;
        public readonly int NextStopWaypointIndex;
        public readonly string NextStationId;
        public readonly string NextStationName;
        public readonly string TerminalStationId;
        public readonly string TerminalStationName;
        public readonly string TurnbackStationId;
        public readonly string TurnbackStationName;

        public VehicleStation(
            string lineId,
            Entity currentStopEntity,
            int currentStopWaypointIndex,
            string currentStationId,
            string currentStationName,
            int nextStopWaypointIndex,
            string nextStationId,
            string nextStationName,
            string terminalStationId,
            string terminalStationName,
            string turnbackStationId,
            string turnbackStationName)
        {
            LineId = lineId;
            CurrentStopEntity = currentStopEntity;
            CurrentStopWaypointIndex = currentStopWaypointIndex;
            CurrentStationId = currentStationId ?? string.Empty;
            CurrentStationName = currentStationName ?? string.Empty;
            NextStopWaypointIndex = nextStopWaypointIndex;
            NextStationId = nextStationId ?? string.Empty;
            NextStationName = nextStationName ?? string.Empty;
            TerminalStationId = terminalStationId ?? string.Empty;
            TerminalStationName = terminalStationName ?? string.Empty;
            TurnbackStationId = turnbackStationId ?? string.Empty;
            TurnbackStationName = turnbackStationName ?? string.Empty;
        }
    }

    internal sealed class LineCache
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

    internal sealed class Stations
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Dictionary<Entity, string> m_CurrentStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_NextStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, int> m_CurrentStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_NextStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, LineCache> m_LineCaches = new Dictionary<Entity, LineCache>();

        internal Stations(BroadcastAccess access, Config config)
        {
            m_Access = access ?? throw new ArgumentNullException(nameof(access));
            m_Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        internal void UpdatePanelState(Entity vehicle, string currentStationName, string nextStationName)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            m_CurrentStationNameByVehicle[vehicle] = currentStationName ?? string.Empty;
            m_NextStationNameByVehicle[vehicle] = nextStationName ?? string.Empty;
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            m_CurrentStationNameByVehicle.Remove(vehicle);
            m_NextStationNameByVehicle.Remove(vehicle);
            m_CurrentStopWaypointIndexByVehicle.Remove(vehicle);
            m_NextStopWaypointIndexByVehicle.Remove(vehicle);
        }

        internal void Clear()
        {
            m_CurrentStationNameByVehicle.Clear();
            m_NextStationNameByVehicle.Clear();
            m_CurrentStopWaypointIndexByVehicle.Clear();
            m_NextStopWaypointIndexByVehicle.Clear();
            m_LineCaches.Clear();
        }

        internal bool TryTriggerContext(
        Entity vehicle,
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        int currentStopWaypointIndex,
        out TriggerContext context)
    {
            return TryTriggerContext(
            vehicle,
            line,
            waypoints,
            currentStopWaypointIndex,
            out context,
            out _);
    }

        internal bool TryTriggerContext(
        Entity vehicle,
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        int currentStopWaypointIndex,
        out TriggerContext context,
        out VehicleStation stationContext)
    {
        context = default;
        stationContext = default;
        if (!m_Config.Enabled)
        {
            return false;
        }
            if (!TryVehicle(
                vehicle,
                line,
                waypoints,
                currentStopWaypointIndex,
                out stationContext))
        {
            return false;
        }

            return TryTriggerContext(stationContext, out context);
    }

        internal bool TryTriggerContext(
        VehicleStation stationContext,
        out TriggerContext context)
    {
            return TryTriggerContext(
            stationContext,
            string.Empty,
            null,
            out context);
    }

        internal bool TryTriggerContext(
        VehicleStation stationContext,
        string overrideTurnbackStationName,
        List<BroadcastWorkbenchStationBindingDto> overrideTurnbackStationBindings,
        out TriggerContext context)
    {
        context = default;
        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
            m_Config.Bindings(stationContext.LineId);
        List<BroadcastWorkbenchStationBindingDto> currentStationBindings =
            Bindings(lineBindings, stationContext.CurrentStationId);
        List<BroadcastWorkbenchStationBindingDto> nextStationBindings =
            Bindings(lineBindings, stationContext.NextStationId);
        List<BroadcastWorkbenchStationBindingDto> terminalStationBindings =
            Bindings(lineBindings, stationContext.TerminalStationId);
        List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings =
            overrideTurnbackStationBindings ?? Bindings(lineBindings, stationContext.TurnbackStationId);
        string turnbackStationName = !string.IsNullOrEmpty(overrideTurnbackStationName)
            ? overrideTurnbackStationName
            : stationContext.TurnbackStationName;

        context = new TriggerContext(
            stationContext.LineId,
            stationContext.CurrentStopEntity,
            stationContext.CurrentStationName,
            stationContext.NextStationName,
            stationContext.TerminalStationName,
            turnbackStationName,
            AssetName(currentStationBindings, 1),
            AssetName(nextStationBindings, 1),
            AssetName(terminalStationBindings, 1),
            AssetName(turnbackStationBindings, 1),
            currentStationBindings,
            nextStationBindings,
            terminalStationBindings,
            turnbackStationBindings);
        return true;
    }

        internal bool TryStation(
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        string stationId,
        out ResolvedStation station)
    {
        station = null;
        if (line == Entity.Null
            || string.IsNullOrWhiteSpace(stationId)
            || !TryCache(line, waypoints, out LineCache cache))
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
                .FirstOrDefault(station => station != null && string.Equals(station.StationId, stationId, StringComparison.Ordinal));
        }
        return station != null;
    }

        internal bool TryStationOnlyContext(
        string lineId,
        ResolvedStation station,
        out TriggerContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(lineId)
            || station == null
            || station.StopEntity == Entity.Null)
        {
            return false;
        }

        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
            m_Config.Bindings(lineId);
        List<BroadcastWorkbenchStationBindingDto> currentStationBindings =
            Bindings(lineBindings, station.StationId);

        context = new TriggerContext(
            lineId,
            station.StopEntity,
            station.Name,
            string.Empty,
            string.Empty,
            string.Empty,
            AssetName(currentStationBindings, 1),
            string.Empty,
            string.Empty,
            string.Empty,
            currentStationBindings,
            null,
            null,
            null);
        return true;
    }

        internal static ResolvedStation TurnbackAfterWaypoint(
        LineCache cache,
        ResolvedStation currentStation)
    {
        if (cache?.TurnbackStations == null || cache.TurnbackStations.Length == 0)
        {
            return null;
        }

        int waypointIndex = currentStation?.WaypointIndex ?? -1;
        ResolvedStation terminalStation =
            cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
        ResolvedStation firstStation = null;
        ResolvedStation nextStation = null;
        List<ResolvedStation> candidates = new List<ResolvedStation>();
        for (int i = 0; i < cache.TurnbackStations.Length; i++)
        {
            ResolvedStation station = cache.TurnbackStations[i];
            if (station != null)
            {
                candidates.Add(station);
            }
        }

        if (terminalStation != null
            && !candidates.Any(candidate => IsSameStation(candidate, terminalStation)))
        {
            candidates.Add(terminalStation);
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            ResolvedStation station = candidates[i];
            if (firstStation == null || station.WaypointIndex < firstStation.WaypointIndex)
            {
                firstStation = station;
            }

            if (station.WaypointIndex > waypointIndex
                && !IsSameStation(station, currentStation)
                && (nextStation == null || station.WaypointIndex < nextStation.WaypointIndex))
            {
                nextStation = station;
            }
        }

        if (nextStation != null)
        {
            return nextStation;
        }

        if (firstStation != null && !IsSameStation(firstStation, currentStation))
        {
            return firstStation;
        }

        for (int i = 0; i < cache.TurnbackStations.Length; i++)
        {
            ResolvedStation station = cache.TurnbackStations[i];
            if (!IsSameStation(station, currentStation))
            {
                return station;
            }
        }

        return firstStation;
    }

    private List<ResolvedStation> BuildResolvedStations(DynamicBuffer<RouteWaypoint> waypoints)
    {
        List<ResolvedStation> stations = new List<ResolvedStation>();
        HashSet<Entity> seenStations = new HashSet<Entity>();
        for (int i = 0; i < waypoints.Length; i++)
        {
            Entity waypoint = waypoints[i].m_Waypoint;
            Entity stopEntity = m_Access.Stop(waypoint);
            Entity anchorEntity = m_Access.Anchor(waypoint);
            Entity identityEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity;
            if (anchorEntity == Entity.Null || identityEntity == Entity.Null || !seenStations.Add(identityEntity))
            {
                continue;
            }

            int order = stations.Count;
            string stationId = m_Access.EnsureSak(anchorEntity);
            stations.Add(new ResolvedStation
            {
                StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                AnchorEntity = anchorEntity,
                WaypointIndex = i,
                Order = order,
                StationId = stationId,
                LegacyStationId = m_Access.StationId(order),
                Name = stopEntity != Entity.Null
                    ? m_Access.StationName(stopEntity)
                    : m_Access.Name(anchorEntity)
            });
        }

        return stations;
    }

    internal bool TryCache(
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        out LineCache cache)
    {
        cache = null;
        if (line == Entity.Null
            || !m_Access.EntityManager.Exists(line)
            || waypoints.Length == 0)
        {
            return false;
        }

        ulong signature = m_Access.Signature(waypoints);
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
            Entity stopEntity = m_Access.Stop(waypoint);
            Entity anchorEntity = m_Access.Anchor(waypoint);
            Entity identityEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity;
            if (anchorEntity == Entity.Null || identityEntity == Entity.Null)
            {
                continue;
            }

            if (!stationOrderByIdentity.TryGetValue(identityEntity, out int order))
            {
                order = stations.Count;
                stationOrderByIdentity[identityEntity] = order;
                string stationId = m_Access.EnsureSak(anchorEntity);
                stations.Add(new ResolvedStation
                {
                    StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                    AnchorEntity = anchorEntity,
                    WaypointIndex = waypointIndex,
                    Order = order,
                    StationId = stationId,
                    LegacyStationId = m_Access.StationId(order),
                    Name = stopEntity != Entity.Null
                        ? m_Access.StationName(stopEntity)
                        : m_Access.Name(anchorEntity)
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
        Dictionary<string, ResolvedStation> stationById =
            new Dictionary<string, ResolvedStation>(StringComparer.Ordinal);
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
                {
                    continue;
                }

                nextDistinctStationWaypointByWaypoint[waypointIndex] = candidateWaypointIndex;
                break;
            }
        }

        cache = new LineCache
        {
            Signature = signature,
            Stations = stationArray,
            StationByWaypoint = stationByWaypoint,
            StationOrderByWaypoint = stationOrderByWaypoint,
            NormalizedStationWaypointByWaypoint = normalizedStationWaypointByWaypoint,
            NextDistinctStationWaypointByWaypoint = nextDistinctStationWaypointByWaypoint,
            StationById = stationById,
            TerminalStationWaypointIndex = stationArray.Length > 0
                ? stationArray[0].WaypointIndex
                : -1
        };
        cache.TurnbackStations = ResolveTurnbackStations(line, waypoints, cache);
        m_LineCaches[line] = cache;
        return stationArray.Length > 0;
    }

        internal string NormalizeRepresentativeStationId(
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        string stationId)
    {
        return stationId ?? string.Empty;
    }

    private ResolvedStation[] ResolveTurnbackStations(
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        LineCache cache)
    {
        if (line == Entity.Null
            || waypoints.Length == 0
            || cache == null
            || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
            || chain == null)
        {
            return Array.Empty<ResolvedStation>();
        }

        List<TrackTurnbackStationBoundary> stationBoundaries = new List<TrackTurnbackStationBoundary>();
        if (!m_Access.TryTurnbacks(chain, stationBoundaries))
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

    private static bool IsSameStation(ResolvedStation left, ResolvedStation right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        return left.Order == right.Order
            || left.StopEntity == right.StopEntity
            || string.Equals(left.StationId, right.StationId, StringComparison.Ordinal);
    }

    private bool TryTurnbackFromBoundary(
        LineCache cache,
        TrackTurnbackStationBoundary stationBoundary,
        out ResolvedStation station)
    {
        station = null;
        if (cache == null)
        {
            return false;
        }

        if (TryStationByWaypoint(cache, stationBoundary.WaypointIndex, out station))
        {
            return true;
        }

        if (stationBoundary.StationEntity == Entity.Null || cache.Stations == null)
        {
            station = null;
            return false;
        }

        Entity anchorEntity = m_Access.AnchorFromStop(stationBoundary.StationEntity);
        string stationId = m_Access.Sak(anchorEntity);
        if (!string.IsNullOrWhiteSpace(stationId)
            && cache.StationById != null
            && cache.StationById.TryGetValue(stationId, out station))
        {
            return true;
        }

        for (int i = 0; i < cache.Stations.Length; i++)
        {
            ResolvedStation candidate = cache.Stations[i];
            if (candidate != null
                && candidate.AnchorEntity == anchorEntity)
            {
                station = candidate;
                return true;
            }
        }

        station = null;
        return false;
    }

    private static bool TryStationByOrder(
        LineCache cache,
        int order,
        out ResolvedStation station)
    {
        station = null;
        if (cache == null
            || cache.Stations == null
            || order < 0
            || order >= cache.Stations.Length)
        {
            return false;
        }

        station = cache.Stations[order];
        return station != null;
    }

    private static bool TryStationByWaypoint(
        LineCache cache,
        int waypointIndex,
        out ResolvedStation station)
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

    private static bool TryNextStationAtOrAfterWaypoint(
        LineCache cache,
        int waypointIndex,
        out ResolvedStation station)
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

    private static bool TryNextStationAfterWaypoint(
        LineCache cache,
        int waypointIndex,
        out ResolvedStation station)
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

    private static bool TryPreviousStationBeforeWaypoint(
        LineCache cache,
        int waypointIndex,
        out ResolvedStation station)
    {
        station = null;
        if (cache == null
            || cache.StationByWaypoint == null
            || cache.StationByWaypoint.Length == 0)
        {
            return false;
        }

        int waypointCount = cache.StationByWaypoint.Length;
        waypointIndex = Mathf.Clamp(waypointIndex, 0, waypointCount - 1);
        for (int offset = 1; offset <= waypointCount; offset++)
        {
            int candidateWaypointIndex = (waypointIndex - offset + waypointCount) % waypointCount;
            station = cache.StationByWaypoint[candidateWaypointIndex];
            if (station != null)
            {
                return true;
            }
        }

        station = null;
        return false;
    }

    private bool TryVehicleTargetWaypoint(
        Entity vehicle,
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        out int waypointIndex)
    {
        waypointIndex = -1;
        if (vehicle == Entity.Null
            || line == Entity.Null
            || !m_Access.EntityManager.HasComponent<Target>(vehicle))
        {
            return false;
        }

        Entity target = m_Access.EntityManager.GetComponentData<Target>(vehicle).m_Target;
        if (target == Entity.Null)
        {
            return false;
        }

        if (m_Access.TryWaypointIndex(line, waypoints, out LineWaypointIndexLookup lookup)
            && lookup != null)
        {
            if (lookup.WaypointIndexByWaypoint.TryGetValue(target, out waypointIndex))
            {
                return true;
            }

            if (lookup.WaypointIndexByStop.TryGetValue(target, out waypointIndex))
            {
                return true;
            }
        }

        if (!m_Access.EntityManager.HasComponent<Waypoint>(target))
        {
            return false;
        }

        waypointIndex = m_Access.EntityManager.GetComponentData<Waypoint>(target).m_Index;
        return waypointIndex >= 0 && waypointIndex < waypoints.Length;
    }

        internal bool TryVehicle(
        Entity vehicle,
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        int preferredCurrentStopWaypointIndex,
        out VehicleStation context)
    {
        context = default;
        if (!TryCache(line, waypoints, out LineCache cache))
        {
            return false;
        }

        ResolvedStation currentStation = null;
        ResolvedStation nextStation = null;
        ResolvedStation cachedCurrentStation = null;

        if (preferredCurrentStopWaypointIndex >= 0)
        {
            TryStationByWaypoint(cache, preferredCurrentStopWaypointIndex, out currentStation);
        }

        if (currentStation == null
            && m_Access.CachedWaypointIndex.TryGetValue(vehicle, out int liveStopWaypointIndex)
            && liveStopWaypointIndex >= 0)
        {
            TryStationByWaypoint(cache, liveStopWaypointIndex, out currentStation);
        }

        if (m_CurrentStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedCurrentWaypointIndex))
        {
            TryStationByWaypoint(cache, cachedCurrentWaypointIndex, out cachedCurrentStation);
        }

        if (currentStation == null
            && TryVehicleTargetWaypoint(vehicle, line, waypoints, out int targetWaypointIndex)
            && TryNextStationAtOrAfterWaypoint(cache, targetWaypointIndex, out ResolvedStation targetNextStation))
        {
            if (cachedCurrentStation != null && targetNextStation.Order <= cachedCurrentStation.Order)
            {
                currentStation = cachedCurrentStation;
                if (targetNextStation.WaypointIndex != cachedCurrentStation.WaypointIndex)
                {
                    nextStation = targetNextStation;
                }
            }
            else if (TryPreviousStationBeforeWaypoint(cache, targetWaypointIndex, out ResolvedStation targetCurrentStation))
            {
                currentStation = targetCurrentStation;
                nextStation = targetNextStation;
            }
        }

        if (currentStation == null)
        {
            currentStation = cachedCurrentStation;
        }

        if (nextStation == null
            && m_NextStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedNextWaypointIndex))
        {
            TryStationByWaypoint(cache, cachedNextWaypointIndex, out nextStation);
        }

        if (currentStation != null
            && (nextStation == null || nextStation.WaypointIndex == currentStation.WaypointIndex))
        {
            TryNextStationAfterWaypoint(cache, currentStation.WaypointIndex, out nextStation);
        }

        if (currentStation == null)
        {
            return false;
        }

        ResolvedStation terminalStation = cache.Stations[0];
        ResolvedStation turnbackStation = TryTurnback(
            vehicle,
            line,
            waypoints,
            cache,
            out ResolvedStation resolvedTurnbackStation)
            ? resolvedTurnbackStation
            : null;
        string lineId = m_Access.DraftKey(m_Access.LineId(line));
        context = new VehicleStation(
            lineId,
            currentStation.StopEntity,
            currentStation.WaypointIndex,
            currentStation.StationId,
            currentStation.Name,
            nextStation?.WaypointIndex ?? -1,
            nextStation?.StationId ?? string.Empty,
            nextStation?.Name ?? string.Empty,
            terminalStation?.StationId ?? string.Empty,
            terminalStation?.Name ?? string.Empty,
            turnbackStation?.StationId ?? string.Empty,
            turnbackStation?.Name ?? string.Empty);

        if (vehicle != Entity.Null)
        {
            m_CurrentStopWaypointIndexByVehicle[vehicle] = context.CurrentStopWaypointIndex;
            if (context.NextStopWaypointIndex >= 0)
            {
                m_NextStopWaypointIndexByVehicle[vehicle] = context.NextStopWaypointIndex;
            }
            else
            {
                m_NextStopWaypointIndexByVehicle.Remove(vehicle);
            }

            m_CurrentStationNameByVehicle[vehicle] = context.CurrentStationName;
            m_NextStationNameByVehicle[vehicle] = context.NextStationName;
        }

        return true;
    }

    private bool TryTurnback(
        Entity vehicle,
        Entity line,
        DynamicBuffer<RouteWaypoint> waypoints,
        LineCache cache,
        out ResolvedStation station)
    {
        station = null;
        if (cache == null
            || line == Entity.Null
            || waypoints.Length == 0
            || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
            || chain == null)
        {
            return false;
        }

        int atomCursorIndex = -1;
        if (vehicle != Entity.Null
            && m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
        {
            atomCursorIndex = cursor.AtomCursorIndex;
        }

        ResolvedStation terminalStation =
            cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
        if (!TryTurnbackBoundaryWithWrap(
                chain,
                atomCursorIndex,
                out TrackTurnbackStationBoundary stationBoundary))
        {
            return false;
        }

        bool resolved = TryTurnbackFromBoundary(cache, stationBoundary, out station);
        bool wrappedToStart = atomCursorIndex >= 0 && stationBoundary.AtomIndex <= atomCursorIndex;
        if (wrappedToStart
            && resolved
            && terminalStation != null
            && !IsSameStation(station, terminalStation))
        {
            station = terminalStation;
            return true;
        }

        return resolved;
    }

    private bool TryTurnbackBoundaryWithWrap(
        LineTrackChain chain,
        int atomCursorIndex,
        out TrackTurnbackStationBoundary stationBoundary)
    {
        stationBoundary = default;
        if (chain == null
            || chain.TurnbackBoundaries == null
            || chain.TurnbackBoundaries.Count == 0)
        {
            return false;
        }

        int cursorAtomIndex = atomCursorIndex >= 0 ? atomCursorIndex : -1;
        bool hasFirstResolvedBoundary = false;
        TrackTurnbackStationBoundary firstResolvedBoundary = default;
        for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
        {
            TurnbackBoundary boundary = chain.TurnbackBoundaries[boundaryIndex];
            if (!m_Access.TryTurnback(chain, boundary, out TrackTurnbackStationBoundary candidate))
            {
                continue;
            }

            if (!hasFirstResolvedBoundary)
            {
                firstResolvedBoundary = candidate;
                hasFirstResolvedBoundary = true;
            }

            if (cursorAtomIndex < 0 || boundary.AtomIndex > cursorAtomIndex)
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

    internal static string AssetName(
        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings,
        string stationId)
    {
        if (lineBindings == null || string.IsNullOrEmpty(stationId))
        {
            return string.Empty;
        }

        if (!lineBindings.TryGetValue(stationId, out List<BroadcastWorkbenchStationBindingDto> bindings)
            || bindings == null
            || bindings.Count == 0)
        {
            return string.Empty;
        }

        return bindings
            .OrderBy(binding => binding?.langIndex ?? int.MaxValue)
            .Select(binding => binding?.assetName ?? string.Empty)
            .FirstOrDefault(assetName => !string.IsNullOrWhiteSpace(assetName))
            ?? string.Empty;
    }

    internal static List<BroadcastWorkbenchStationBindingDto> Bindings(
        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings,
        string stationId)
    {
        if (lineBindings == null || string.IsNullOrEmpty(stationId))
        {
            return null;
        }

        if (!lineBindings.TryGetValue(stationId, out List<BroadcastWorkbenchStationBindingDto> bindings)
            || bindings == null
            || bindings.Count == 0)
        {
            return null;
        }

        return bindings;
    }

    internal static string AssetName(
        List<BroadcastWorkbenchStationBindingDto> bindings,
        int langIndex)
    {
        if (bindings == null || bindings.Count == 0)
        {
            return string.Empty;
        }

        int targetLangIndex = langIndex > 0 ? langIndex : 1;
        BroadcastWorkbenchStationBindingDto exactMatch = bindings.FirstOrDefault(binding =>
            binding != null
            && binding.langIndex == targetLangIndex
            && !string.IsNullOrWhiteSpace(binding.assetName));
        if (exactMatch != null)
        {
            return exactMatch.assetName ?? string.Empty;
        }

        if (targetLangIndex != 1)
        {
            BroadcastWorkbenchStationBindingDto fallbackMatch = bindings.FirstOrDefault(binding =>
                binding != null
                && binding.langIndex == 1
                && !string.IsNullOrWhiteSpace(binding.assetName));
            if (fallbackMatch != null)
            {
                return fallbackMatch.assetName ?? string.Empty;
            }
        }

        return string.Empty;
    }

    internal bool TryPanelContext(
        Entity vehicle,
        Entity line,
        out string currentStationName,
        out string nextStationName,
        out string terminalStationName)
    {
        currentStationName = m_CurrentStationNameByVehicle.TryGetValue(vehicle, out string currentName)
            ? currentName ?? string.Empty
            : string.Empty;
        nextStationName = m_NextStationNameByVehicle.TryGetValue(vehicle, out string nextName)
            ? nextName ?? string.Empty
            : string.Empty;
        terminalStationName = string.Empty;

        if (line == Entity.Null || !m_Access.EntityManager.HasBuffer<RouteWaypoint>(line))
        {
            return !string.IsNullOrEmpty(currentStationName) || !string.IsNullOrEmpty(nextStationName);
        }

        DynamicBuffer<RouteWaypoint> waypoints = m_Access.EntityManager.GetBuffer<RouteWaypoint>(line, true);
        if (TryVehicle(
                vehicle,
                line,
                waypoints,
                -1,
                out VehicleStation context))
        {
            currentStationName = context.CurrentStationName;
            nextStationName = context.NextStationName;
            terminalStationName = context.TerminalStationName;
        }

        return !string.IsNullOrEmpty(currentStationName)
            || !string.IsNullOrEmpty(nextStationName)
            || !string.IsNullOrEmpty(terminalStationName);
    }


    }
}
