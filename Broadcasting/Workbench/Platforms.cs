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
    internal sealed class Platforms : ModuleBase
    {
        internal Platforms(Context context) : base(context) { }

                public string SaveBroadcastPlatformAnnouncementJson(string requestJson)
                {
                    return SaveCore(requestJson, copyToAllStations: false);
                }

                public string CopyBroadcastPlatformAnnouncementToAllStationsJson(string requestJson)
                {
                    return SaveCore(requestJson, copyToAllStations: true);
                }

                internal string SaveCore(string requestJson, bool copyToAllStations)
                {
                    BroadcastWorkbenchSavePlatformAnnouncementResult result = new BroadcastWorkbenchSavePlatformAnnouncementResult
                    {
                        success = false,
                        error = string.Empty
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(
                            requestJson,
                            copyToAllStations
                                ? "copyBroadcastPlatformAnnouncementToAllStations"
                                : "saveBroadcastPlatformAnnouncement");
                        if (scope.Mode == TransitMode.Bus)
                        {
                            throw new InvalidOperationException("Bus broadcast does not support platform announcements.");
                        }
                        using (UseScope(scope))
                        {
                        BroadcastWorkbenchSavePlatformAnnouncementRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchSavePlatformAnnouncementRequest>(requestJson);
                        string lineId = scope.NormalizeLineId(request?.lineId);
                        string stationId = request?.stationId ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(stationId))
                        {
                            result.error = "Line or station is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }
                        if (!scope.MatchesLineId(lineId))
                        {
                            result.error = "Line does not belong to mode " + scope.Token + ".";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        List<WorkbenchLineRuntime> runtimeLines = Lines();
                        WorkbenchLineRuntime activeRuntime = FindLine(runtimeLines, lineId);
                        List<StationGroup> stationGroups = new List<StationGroup>();
                        if (activeRuntime != null)
                        {
                            m_Ctx.Drafts.EnsureLine(lineId, activeRuntime.Entity, out stationGroups);
                        }
                        BroadcastWorkbenchPlatformAnnouncementDto announcement =
                            Normalize(lineId, stationId, request?.stationName, request?.title, request?.uiTriggerId, request?.enabled == true, request?.nodes);
                        m_Ctx.Rules.ValidateNodeCatalog(announcement.nodes);
                        Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements =
                            EnsureDraft(lineId);

                        if (copyToAllStations)
                        {
                            foreach (StationGroup group in stationGroups)
                            {
                                string targetStationId = group?.Representative?.id ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(targetStationId))
                                {
                                    continue;
                                }

                                BroadcastWorkbenchPlatformAnnouncementDto clonedAnnouncement = Clone(
                                    announcement,
                                    lineId,
                                    targetStationId,
                                    m_Ctx.Snapshot.StationName(group));
                                lineAnnouncements[Key(
                                    targetStationId,
                                    clonedAnnouncement.uiTriggerId)] = clonedAnnouncement;
                            }
                        }
                        else
                        {
                            lineAnnouncements[Key(
                                stationId,
                                announcement.uiTriggerId)] = announcement;
                        }

                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;
                        result.snapshot = m_Ctx.Snapshot.Build(scope, lineId);
                        global::RapidTransitMod.Workbenches.UiEvents.Push(result.snapshot);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("SaveBroadcastPlatformAnnouncementJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> EnsureDraft(string lineId)
                {
                    string lineKey = lineId ?? string.Empty;
                    if (!DraftPlatforms.TryGetValue(lineKey, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements))
                    {
                        announcements = new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
                        DraftPlatforms[lineKey] = announcements;
                    }

                    return announcements;
                }

                internal Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> Applied(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || !AppliedPlatforms.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements)
                        || announcements == null)
                    {
                        return null;
                    }

                    return announcements;
                }

                internal Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> Draft(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId))
                    {
                        return null;
                    }

                    if (DraftPlatforms.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements)
                        && announcements != null)
                    {
                        return announcements;
                    }

                    return Applied(lineId);
                }

                internal BroadcastWorkbenchPlatformAnnouncementDto[] DraftRows(
                    string lineId,
                    List<StationGroup> stationGroups)
                {
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements =
                        Draft(lineId);
                    if (string.IsNullOrEmpty(lineId)
                        || stationGroups == null
                        || stationGroups.Count == 0
                        || announcements == null
                        || announcements.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
                    }

                    Dictionary<string, string> stationNameBySak =
                        m_Ctx.Snapshot.StationNames(stationGroups);

                    List<BroadcastWorkbenchPlatformAnnouncementDto> result = new List<BroadcastWorkbenchPlatformAnnouncementDto>();
                    foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in announcements)
                    {
                        BroadcastWorkbenchPlatformAnnouncementDto announcement = entry.Value;
                        if (announcement == null || string.IsNullOrWhiteSpace(announcement.stationId))
                        {
                            continue;
                        }

                        if (!stationNameBySak.TryGetValue(announcement.stationId, out string stationName))
                        {
                            continue;
                        }

                        result.Add(Clone(
                            announcement,
                            lineId,
                            announcement.stationId,
                            stationName));
                    }

                    return result.ToArray();
                }

                internal static void RestoreInto(
                    Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> target,
                    BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedAnnouncements)
                {
                    if (persistedAnnouncements == null)
                    {
                        return;
                    }

                    for (int i = 0; i < persistedAnnouncements.Length; i++)
                    {
                        BroadcastWorkbenchPersistedPlatformAnnouncementState lineState = persistedAnnouncements[i];
                        if (lineState == null || string.IsNullOrWhiteSpace(lineState.lineId) || lineState.announcements == null)
                        {
                            continue;
                        }

                        Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements =
                            new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
                        for (int j = 0; j < lineState.announcements.Length; j++)
                        {
                            BroadcastWorkbenchPlatformAnnouncementDto announcement = lineState.announcements[j];
                            if (announcement == null || string.IsNullOrWhiteSpace(announcement.stationId))
                            {
                                continue;
                            }

                            BroadcastWorkbenchPlatformAnnouncementDto normalizedAnnouncement = Normalize(
                                lineState.lineId,
                                announcement.stationId,
                                announcement.stationName,
                                announcement.title,
                                announcement.uiTriggerId,
                                announcement.enabled,
                                announcement.nodes);
                            lineAnnouncements[Key(
                                normalizedAnnouncement.stationId,
                                normalizedAnnouncement.uiTriggerId)] = normalizedAnnouncement;
                        }

                        if (lineAnnouncements.Count > 0)
                        {
                            target[lineState.lineId] = lineAnnouncements;
                        }
                    }
                }

                internal static void Copy(
                    Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source,
                    Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> target)
                {
                    foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> entry in source)
                    {
                        if (entry.Value == null || entry.Value.Count == 0)
                        {
                            continue;
                        }

                        target[entry.Key] = CloneLine(entry.Value);
                    }
                }

                internal static bool Same(
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> left,
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> right)
                {
                    string leftJson = global::RapidTransitMod.Workbenches.Json.Write(Flatten(left));
                    string rightJson = global::RapidTransitMod.Workbenches.Json.Write(Flatten(right));
                    return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
                }

                internal static BroadcastWorkbenchPlatformAnnouncementDto[] Flatten(
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                {
                    if (lineAnnouncements == null || lineAnnouncements.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
                    }

                    return lineAnnouncements
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => Clone(
                            entry.Value,
                            entry.Value?.lineId ?? string.Empty,
                            entry.Value?.stationId ?? string.Empty,
                            entry.Value?.stationName ?? string.Empty))
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.stationId))
                        .ToArray();
                }

                internal static BroadcastWorkbenchPlatformAnnouncementDto Clone(
                    BroadcastWorkbenchPlatformAnnouncementDto announcement,
                    string lineId,
                    string stationId,
                    string stationName)
                {
                    if (announcement == null)
                    {
                        return Normalize(lineId, stationId, stationName, string.Empty, "platform_idle_clear", false, null);
                    }

                    return Normalize(
                        lineId,
                        stationId,
                        string.IsNullOrWhiteSpace(stationName) ? announcement.stationName : stationName,
                        announcement.title,
                        announcement.uiTriggerId,
                        announcement.enabled,
                        announcement.nodes);
                }

                internal static BroadcastWorkbenchPlatformAnnouncementDto Normalize(
                    string lineId,
                    string stationId,
                    string stationName,
                    string title,
                    string uiTriggerId,
                    bool enabled,
                    IEnumerable<BroadcastWorkbenchRuleNodeDto> nodes)
                {
                    string normalizedUiTriggerId = UiTrigger(uiTriggerId);
                    return new BroadcastWorkbenchPlatformAnnouncementDto
                    {
                        lineId = lineId ?? string.Empty,
                        stationId = stationId ?? string.Empty,
                        stationName = stationName ?? string.Empty,
                        title = title ?? string.Empty,
                        uiTriggerId = normalizedUiTriggerId,
                        enabled = enabled,
                        triggerId = RuntimeTrigger(normalizedUiTriggerId),
                        cooldownGameMinutes = 20,
                        nodes = nodes == null
                            ? Array.Empty<BroadcastWorkbenchRuleNodeDto>()
                            : nodes
                                .Select(Rules.CloneNode)
                                .Where(node => node != null
                                    && !string.IsNullOrWhiteSpace(node.id)
                                    && !string.IsNullOrWhiteSpace(node.type))
                                .ToArray()
                    };
                }

                internal static string UiTrigger(string uiTriggerId)
                {
                    switch ((uiTriggerId ?? string.Empty).Trim())
                    {
                        case "approach_station":
                        case "platform_approach_station":
                            return "approach_station";
                        case "platform_idle_clear":
                        default:
                            return "platform_idle_clear";
                    }
                }

                internal static string RuntimeTrigger(string uiTriggerId)
                {
                    return string.Equals(uiTriggerId, "approach_station", StringComparison.Ordinal)
                        ? "platform_approach_station"
                        : "platform_idle_clear";
                }

                internal static Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> CloneLine(
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> source)
                {
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> clone =
                        new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
                    if (source == null)
                    {
                        return clone;
                    }

                    foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in source)
                    {
                        BroadcastWorkbenchPlatformAnnouncementDto announcement = Clone(
                            entry.Value,
                            entry.Value?.lineId ?? string.Empty,
                            entry.Value?.stationId ?? string.Empty,
                            entry.Value?.stationName ?? string.Empty);
                        string storageKey = Key(
                            announcement?.stationId,
                            announcement?.uiTriggerId);
                        if (!string.IsNullOrWhiteSpace(storageKey))
                        {
                            clone[storageKey] = announcement;
                        }
                    }

                    return clone;
                }

                internal static string Key(string stationId, string uiTriggerId)
                {
                    string normalizedStationId = stationId ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(normalizedStationId))
                    {
                        return string.Empty;
                    }

                    string normalizedUiTriggerId = UiTrigger(uiTriggerId);
                    return normalizedStationId + "|" + normalizedUiTriggerId;
                }
    }
}
