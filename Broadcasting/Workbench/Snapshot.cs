using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ATL;
using Colossal.Core;
using Game;
using Game.Audio;
using Game.UI.InGame;
using Game.UI.Menu;
using Game.Routes;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
using RapidTransitMod;
using RapidTransitMod.Broadcasting;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Snapshot : ModuleBase
    {
        internal Snapshot(Context context) : base(context) { }

                public string LoadBroadcastWorkbenchSnapshotJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "loadBroadcastSnapshot");
                    string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
                    LoadWorkbench();
                    using (UseScope(scope))
                    {
                        return global::RapidTransitMod.Workbenches.Json.Write(Build(scope, preferredLineId));
                    }
                }

                public string RefreshBroadcastWorkbenchSnapshotJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "refreshBroadcastSnapshot");
                    string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
                    if (Workbenches.ModeRequest.ReadNamesOnly(requestJson))
                    {
                        using (UseScope(scope))
                        {
                            return global::RapidTransitMod.Workbenches.Json.Write(BuildNames(scope, preferredLineId));
                        }
                    }

                    LoadWorkbench();
                    using (UseScope(scope))
                    {
                        return global::RapidTransitMod.Workbenches.Json.Write(Build(scope, preferredLineId));
                    }
                }

                private BroadcastWorkbenchSnapshot BuildNames(ModeScope scope, string lineId)
                {
                    List<WorkbenchLineRuntime> runtimeLines = Lines()
                        .Where(line => line != null && scope.MatchesLineId(line.Id))
                        .ToList();
                    WorkbenchLineRuntime activeRuntime = FindLine(runtimeLines, lineId);
                    List<StationGroup> stationGroups = activeRuntime != null
                        ? Groups(activeRuntime.Entity)
                        : new List<StationGroup>();

                    return new BroadcastWorkbenchSnapshot
                    {
                        mode = scope.Token,
                        selectedLineId = activeRuntime?.Id ?? string.Empty,
                        lines = runtimeLines.Select(Line).ToArray(),
                        stations = stationGroups
                            .Where(group => group?.Representative != null)
                            .Select(group => CloneDispatchWorkbenchStationDto(
                                group.Representative,
                                group.StopEntity,
                                group.AnchorEntity))
                            .ToArray(),
                        turnbackPoints = Array.Empty<BroadcastWorkbenchTurnbackPointDto>(),
                        stationBindings = Array.Empty<BroadcastWorkbenchStationBindingDto>(),
                        rules = Array.Empty<BroadcastWorkbenchRuleDto>(),
                        platformAnnouncements = Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>(),
                        assetDirectory = string.Empty,
                        assets = Array.Empty<BroadcastWorkbenchAssetDto>(),
                        version = m_WorkbenchSnapshotVersion.ToString(),
                        sourceMode = "game-backend-names",
                        warnings = Array.Empty<string>()
                    };
                }

                internal BroadcastWorkbenchSnapshot Build(string lineId)
                {
                    return Build(CurrentScope, lineId);
                }

                internal BroadcastWorkbenchSnapshot Build(ModeScope scope, string lineId)
                {
                    LoadWorkbench();

                    using (UseScope(scope))
                    {
                    List<WorkbenchLineRuntime> runtimeLines = Lines()
                        .Where(line => line != null && scope.MatchesLineId(line.Id))
                        .ToList();
                    WorkbenchLineRuntime activeRuntime = FindLine(runtimeLines, lineId);
                    List<StationGroup> stationGroups = new List<StationGroup>();
                    if (activeRuntime != null
                        && m_Ctx.Drafts.EnsureLine(activeRuntime.Id, activeRuntime.Entity, out List<StationGroup> migratedGroups))
                    {
                        stationGroups = migratedGroups;
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                    }
                    else if (activeRuntime != null)
                    {
                        stationGroups = m_Ctx.Snapshot.Groups(activeRuntime.Entity);
                    }
                    m_Ctx.Conflicts.ApplyPending(stationGroups, activeRuntime?.Id ?? string.Empty);
                    BroadcastWorkbenchStationBindingDto[] stationBindings = activeRuntime != null
                        ? m_Ctx.Bindings.DraftRows(activeRuntime.Id, stationGroups)
                        : Array.Empty<BroadcastWorkbenchStationBindingDto>();
                    BroadcastWorkbenchRuleDto[] rules = activeRuntime != null
                        ? m_Ctx.Rules.DraftRows(activeRuntime.Id)
                        : Array.Empty<BroadcastWorkbenchRuleDto>();
                    bool supportsPlatforms = scope.Mode != TransitMode.Bus;
                    BroadcastWorkbenchPlatformAnnouncementDto[] platformAnnouncements = supportsPlatforms && activeRuntime != null
                        ? m_Ctx.Platforms.DraftRows(activeRuntime.Id, stationGroups)
                        : Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
                    string activeLineId = activeRuntime?.Id ?? string.Empty;
                    bool lineDraftDirty = false;
                    bool lineApplied = !string.IsNullOrEmpty(activeLineId)
                        && AppliedLines.Contains(activeLineId);
                    bool volumeDirty = false;
                    bool draftDirty = false;
                    bool draftApplied = lineApplied
                        && !draftDirty;

                    return new BroadcastWorkbenchSnapshot
                    {
                        mode = scope.Token,
                        selectedLineId = activeLineId,
                        lines = runtimeLines.Select(Line).ToArray(),
                        stations = stationGroups
                            .Where(group => group?.Representative != null)
                            .Select(group => CloneDispatchWorkbenchStationDto(
                                group.Representative,
                                group.StopEntity,
                                group.AnchorEntity))
                            .ToArray(),
                        turnbackPoints = supportsPlatforms && activeRuntime != null
                            ? Turnbacks(activeRuntime.Entity, stationGroups)
                            : Array.Empty<BroadcastWorkbenchTurnbackPointDto>(),
                        stationBindings = stationBindings,
                        rules = rules,
                        platformAnnouncements = platformAnnouncements,
                        assetDirectory = AssetFolder,
                        assets = Catalog.Select(Assets.CloneAsset).ToArray(),
                        version = m_WorkbenchSnapshotVersion.ToString(),
                        sourceMode = "game-backend",
                        lineApplied = lineApplied,
                        lineDraftDirty = lineDraftDirty,
                        volumeDirty = volumeDirty,
                        draftApplied = draftApplied,
                        draftDirty = draftDirty,
                        volume = Preview.Clamp(AppliedVol),
                        warnings = activeRuntime != null
                            ? m_Ctx.Snapshot.Warnings(activeRuntime.Id)
                            : Array.Empty<string>()
                    };
                    }
                }

                internal WorkbenchLineRuntime LineRuntime(string lineId)
                {
                    if (string.IsNullOrWhiteSpace(lineId))
                    {
                        return null;
                    }

                    List<WorkbenchLineRuntime> runtimeLines = Lines();
                    return runtimeLines.FirstOrDefault(runtime =>
                        runtime != null && string.Equals(runtime.Id, lineId, StringComparison.Ordinal));
                }

                internal Dictionary<string, string> StationNames(
                    List<StationGroup> stationGroups)
                {
                    Dictionary<string, string> stationNameBySak =
                        new Dictionary<string, string>(StringComparer.Ordinal);
                    if (stationGroups == null)
                    {
                        return stationNameBySak;
                    }

                    for (int i = 0; i < stationGroups.Count; i++)
                    {
                        StationGroup group = stationGroups[i];
                        string sak = group?.Representative?.id ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(sak))
                        {
                            stationNameBySak[sak] = StationName(group);
                        }
                    }

                    return stationNameBySak;
                }

                internal string StationName(StationGroup group)
                {
                    if (group == null)
                    {
                        return string.Empty;
                    }

                    return StationName(
                        group.StopEntity,
                        group.AnchorEntity,
                        group.Representative?.name ?? string.Empty);
                }

                internal string StationName(
                    Entity stopEntity,
                    Entity anchorEntity,
                    string fallbackName)
                {
                    string name = string.Empty;
                    if (stopEntity != Entity.Null)
                    {
                        name = m_Access.StationName(stopEntity);
                    }
                    if (string.IsNullOrWhiteSpace(name) && anchorEntity != Entity.Null)
                    {
                        name = m_Access.Name(anchorEntity);
                    }
                    return string.IsNullOrWhiteSpace(name)
                        ? fallbackName ?? string.Empty
                        : name;
                }

                internal List<StationGroup> Groups(Entity line)
                {
                    List<StationGroup> groups = new List<StationGroup>();
                    if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                    {
                        return groups;
                    }

                    DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                    if (!m_Announcements.Stations.TryCache(line, waypoints, out LineCache cache)
                        || cache?.Stations == null
                        || cache.Stations.Length == 0)
                    {
                        return groups;
                    }

                    Dictionary<string, StationGroup> groupsByKey =
                        new Dictionary<string, StationGroup>(StringComparer.Ordinal);

                    for (int i = 0; i < cache.Stations.Length; i++)
                    {
                        ResolvedStation station = cache.Stations[i];
                        if (station == null || string.IsNullOrWhiteSpace(station.StationId))
                        {
                            continue;
                        }

                        string key = station.StationId;

                        if (!groupsByKey.TryGetValue(key, out StationGroup group))
                        {
                            group = new StationGroup
                            {
                                Key = key,
                                StopEntity = station.StopEntity,
                                AnchorEntity = station.AnchorEntity,
                                Representative = new DispatchWorkbenchStationDto
                                {
                                    id = station.StationId,
                                    name = station.Name ?? string.Empty,
                                    order = groups.Count,
                                    distance = 0f,
                                    hasSiding = false,
                                    conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>()
                                }
                            };
                            groupsByKey[key] = group;
                            groups.Add(group);
                        }

                        if (!string.IsNullOrWhiteSpace(station.LegacyStationId))
                        {
                            group.StationIds.Add(station.LegacyStationId);
                        }
                    }

                    return groups;
                }

                internal DispatchWorkbenchStationDto CloneDispatchWorkbenchStationDto(DispatchWorkbenchStationDto station)
                {
                    return CloneDispatchWorkbenchStationDto(station, Entity.Null, Entity.Null);
                }

                internal DispatchWorkbenchStationDto CloneDispatchWorkbenchStationDto(
                    DispatchWorkbenchStationDto station,
                    Entity stopEntity,
                    Entity anchorEntity)
                {
                    if (station == null)
                    {
                        return null;
                    }

                    string name = StationName(stopEntity, anchorEntity, station.name);

                    return new DispatchWorkbenchStationDto
                    {
                        id = station.id ?? string.Empty,
                        name = name,
                        order = station.order,
                        distance = station.distance,
                        hasSiding = station.hasSiding,
                        conflictAssets = station.conflictAssets == null
                            ? null
                            : station.conflictAssets
                                .Select(conflict => conflict == null
                                    ? null
                                    : new DispatchWorkbenchStationConflictDto
                                    {
                                        assetName = conflict.assetName ?? string.Empty,
                                        suggestedLang = conflict.suggestedLang ?? string.Empty
                                    })
                                .Where(conflict => conflict != null)
                                .ToArray()
                    };
                }

                internal bool TryTurnback(
                    Entity line,
                    List<StationGroup> stationGroups,
                    out StationGroup stationGroup)
                {
                    stationGroup = null;
                    if (line == Entity.Null
                        || stationGroups == null
                        || stationGroups.Count == 0
                        || !EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line))
                    {
                        return false;
                    }

                    DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                        EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                    if (!m_Announcements.Stations.TryCache(line, waypoints, out LineCache cache)
                        || cache?.TurnbackStations == null
                        || cache.TurnbackStations.Length == 0)
                    {
                        return false;
                    }

                    for (int i = 0; i < cache.TurnbackStations.Length; i++)
                    {
                        ResolvedStation turnbackStation = cache.TurnbackStations[i];
                        if (turnbackStation != null
                            && TryGroup(stationGroups, turnbackStation.StationId, out stationGroup))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                internal BroadcastWorkbenchTurnbackPointDto[] Turnbacks(
                    Entity line,
                    List<StationGroup> stationGroups)
                {
                    if (line == Entity.Null
                        || stationGroups == null
                        || !EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line))
                    {
                        return Array.Empty<BroadcastWorkbenchTurnbackPointDto>();
                    }

                    DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                        EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                    if (!m_Announcements.Stations.TryCache(line, waypoints, out LineCache cache)
                        || cache?.TurnbackStations == null)
                    {
                        return Array.Empty<BroadcastWorkbenchTurnbackPointDto>();
                    }

                    List<BroadcastWorkbenchTurnbackPointDto> points = new List<BroadcastWorkbenchTurnbackPointDto>();
                    for (int i = 0; i < cache.TurnbackStations.Length; i++)
                    {
                        ResolvedStation turnbackStation = cache.TurnbackStations[i];
                        StationGroup stationGroup = null;
                        bool resolved = turnbackStation != null
                            && TryGroup(
                                stationGroups,
                                turnbackStation.StationId,
                                out stationGroup);
                        points.Add(new BroadcastWorkbenchTurnbackPointDto
                        {
                            index = points.Count + 1,
                            stationId = resolved
                                ? stationGroup?.Representative?.id ?? string.Empty
                                : turnbackStation?.StationId ?? string.Empty,
                            stationName = resolved
                                ? stationGroup?.Representative?.name ?? string.Empty
                                : turnbackStation?.Name ?? string.Empty,
                            resolved = resolved
                        });
                    }

                    StationGroup terminalStationGroup =
                        stationGroups.Count > 0 ? stationGroups[0] : null;
                    bool hasResolvedTurnback = points.Any(point => point != null && point.resolved);
                    bool terminalAlreadyIncluded = terminalStationGroup != null
                        && points.Any(point => point != null
                            && point.resolved
                            && string.Equals(point.stationId, terminalStationGroup.Representative?.id ?? string.Empty, StringComparison.Ordinal));
                    if (hasResolvedTurnback
                        && terminalStationGroup?.Representative != null
                        && !terminalAlreadyIncluded)
                    {
                        points.Add(new BroadcastWorkbenchTurnbackPointDto
                        {
                            index = points.Count + 1,
                            stationId = terminalStationGroup.Representative.id ?? string.Empty,
                            stationName = StationName(terminalStationGroup),
                            resolved = true
                        });
                    }

                    return points.ToArray();
                }

                internal static bool TryGroup(
                    List<StationGroup> stationGroups,
                    string stationId,
                    out StationGroup stationGroup)
                {
                    stationGroup = null;
                    if (stationGroups == null || stationGroups.Count == 0 || string.IsNullOrWhiteSpace(stationId))
                    {
                        return false;
                    }

                    stationGroup = stationGroups.FirstOrDefault(group =>
                        group != null
                        && string.Equals(group.Representative?.id ?? string.Empty, stationId, StringComparison.Ordinal));
                    return stationGroup != null;
                }

                internal string[] Warnings(string lineId)
                {
                    return Array.Empty<string>();
                }

                internal DispatchWorkbenchLineDto Line(WorkbenchLineRuntime runtime)
                {
                    if (runtime == null)
                    {
                        return new DispatchWorkbenchLineDto();
                    }

                    string liveName = m_Access.Name(runtime.Entity);
                    return new DispatchWorkbenchLineDto
                    {
                        id = runtime.Id,
                        sourceLineId = runtime.Entity.Index.ToString(),
                        name = string.IsNullOrEmpty(liveName) ? runtime.Name : liveName,
                        kind = runtime.Kind,
                        direction = "up",
                        stationCount = runtime.StationCount,
                        color = runtime.Color,
                        originStationId = runtime.OriginStationId,
                        originStationName = runtime.OriginStationName,
                        transportType = runtime.TransportType
                    };
                }
    }
}
