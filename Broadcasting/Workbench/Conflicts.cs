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
using IoPath = System.IO.Path;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Conflicts : ModuleBase
    {
        internal Conflicts(Context context) : base(context) { }

                public string AutoBindBroadcastStationMappingsJson(string requestJson)
                {
                    BroadcastWorkbenchAutoBindStationMappingsResult result = new BroadcastWorkbenchAutoBindStationMappingsResult
                    {
                        success = false,
                        boundCount = 0,
                        error = string.Empty
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "autoBindBroadcastStationMappings");
                        using (UseScope(scope))
                        {

                        string resolvedLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadLine(requestJson));
                        if (string.IsNullOrEmpty(resolvedLineId))
                        {
                            result.error = "Line is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }
                        if (!scope.MatchesLineId(resolvedLineId))
                        {
                            result.error = "Line does not belong to mode " + scope.Token + ".";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        List<WorkbenchLineRuntime> runtimeLines = Lines();
                        WorkbenchLineRuntime activeRuntime = runtimeLines.FirstOrDefault(runtime =>
                            runtime != null
                            && string.Equals(runtime.Id, resolvedLineId, StringComparison.Ordinal));
                        if (activeRuntime == null)
                        {
                            result.error = "Selected line was not found.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        m_Ctx.Drafts.EnsureLine(resolvedLineId, activeRuntime.Entity, out List<StationGroup> stationGroups);
                        if (stationGroups.Count == 0 || Catalog.Count == 0)
                        {
                            Clear(resolvedLineId);
                            result.success = true;
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                            m_Ctx.Bindings.EnsureDraft(resolvedLineId);
                        Clear(resolvedLineId);
                        for (int i = 0; i < stationGroups.Count; i++)
                        {
                            StationGroup group = stationGroups[i];
                            string stationName = m_Ctx.Snapshot.StationName(group);
                            string targetStationId = group?.Representative?.id ?? string.Empty;
                            if (group == null || string.IsNullOrWhiteSpace(targetStationId))
                            {
                                continue;
                            }

                            string stationKey = MatchKey(stationName);
                            List<DispatchWorkbenchStationConflictDto> matchedAssets =
                                Candidates(stationKey, null);
                            if (string.IsNullOrEmpty(stationKey)
                                || matchedAssets == null
                                || matchedAssets.Count != 1)
                            {
                                continue;
                            }

                            string matchedAssetName = matchedAssets[0]?.assetName ?? string.Empty;
                            if (string.IsNullOrEmpty(matchedAssetName))
                            {
                                continue;
                            }

                            bool changed = false;
                            if (!lineBindings.TryGetValue(targetStationId, out List<BroadcastWorkbenchStationBindingDto> currentBindings)
                                || currentBindings == null
                                || !currentBindings.Any(binding => binding != null && !string.IsNullOrWhiteSpace(binding.assetName)))
                            {
                                lineBindings[targetStationId] = new List<BroadcastWorkbenchStationBindingDto>
                                {
                                    new BroadcastWorkbenchStationBindingDto
                                    {
                                        stationId = targetStationId,
                                        langIndex = 1,
                                        assetName = matchedAssetName
                                    }
                                };
                                changed = true;
                            }

                            if (!changed)
                            {
                                continue;
                            }

                            result.boundCount++;
                        }

                        Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup = ConflictMap();
                        ApplyConflicts(stationGroups, conflictLookup, resolvedLineId);
                        Store(resolvedLineId, stationGroups);

                        result.success = true;
                        if (result.boundCount <= 0)
                        {
                            if (lineBindings.Count == 0)
                            {
                                DraftBindings.Remove(resolvedLineId);
                            }

                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, resolvedLineId));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("AutoBindBroadcastStationMappingsJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal Dictionary<string, string> AssetMap()
                {
                    Dictionary<string, string> assetByStationKey = new Dictionary<string, string>(StringComparer.Ordinal);
                    HashSet<string> ambiguousKeys = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < Catalog.Count; i++)
                    {
                        BroadcastWorkbenchAssetDto asset = Catalog[i];
                        string assetName = asset?.name ?? string.Empty;
                        string stationKey = MatchKey(assetName);
                        if (string.IsNullOrEmpty(stationKey) || ambiguousKeys.Contains(stationKey))
                        {
                            continue;
                        }

                        if (assetByStationKey.ContainsKey(stationKey))
                        {
                            assetByStationKey.Remove(stationKey);
                            ambiguousKeys.Add(stationKey);
                            continue;
                        }

                        assetByStationKey[stationKey] = assetName;
                    }

                    return assetByStationKey;
                }

                internal Dictionary<string, List<DispatchWorkbenchStationConflictDto>> ConflictMap()
                {
                    Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictsByStationKey =
                        new Dictionary<string, List<DispatchWorkbenchStationConflictDto>>(StringComparer.Ordinal);
                    for (int i = 0; i < Catalog.Count; i++)
                    {
                        BroadcastWorkbenchAssetDto asset = Catalog[i];
                        string assetName = asset?.name ?? string.Empty;
                        string stationKey = MatchKey(assetName);
                        if (string.IsNullOrEmpty(stationKey))
                        {
                            continue;
                        }

                        if (!conflictsByStationKey.TryGetValue(stationKey, out List<DispatchWorkbenchStationConflictDto> conflicts))
                        {
                            conflicts = new List<DispatchWorkbenchStationConflictDto>();
                            conflictsByStationKey[stationKey] = conflicts;
                        }

                        conflicts.Add(new DispatchWorkbenchStationConflictDto
                        {
                            assetName = assetName,
                            suggestedLang = string.Empty
                        });
                    }

                    List<string> nonConflictKeys = null;
                    foreach (KeyValuePair<string, List<DispatchWorkbenchStationConflictDto>> entry in conflictsByStationKey)
                    {
                        if (entry.Value == null || entry.Value.Count <= 1)
                        {
                            nonConflictKeys ??= new List<string>();
                            nonConflictKeys.Add(entry.Key);
                            continue;
                        }

                        entry.Value.Sort((left, right) => string.Compare(left?.assetName, right?.assetName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (nonConflictKeys != null)
                    {
                        for (int i = 0; i < nonConflictKeys.Count; i++)
                        {
                            conflictsByStationKey.Remove(nonConflictKeys[i]);
                        }
                    }

                    return conflictsByStationKey;
                }

                internal void ApplyConflicts(
                    List<StationGroup> stationGroups,
                    Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup,
                    string lineId)
                {
                    if (stationGroups == null || stationGroups.Count == 0)
                    {
                        return;
                    }

                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                        m_Ctx.Bindings.Draft(lineId);
                    for (int i = 0; i < stationGroups.Count; i++)
                    {
                        StationGroup group = stationGroups[i];
                        if (group?.Representative == null)
                        {
                            continue;
                        }

                        List<DispatchWorkbenchStationConflictDto> conflicts =
                            Candidates(MatchKey(m_Ctx.Snapshot.StationName(group)), conflictLookup);
                        if (string.IsNullOrEmpty(group.Key)
                            || conflicts == null
                            || conflicts.Count <= 1)
                        {
                            group.Representative.conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>();
                            continue;
                        }

                        HashSet<string> boundAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (lineBindings != null)
                        {
                            if (lineBindings.TryGetValue(group.Representative.id ?? string.Empty, out List<BroadcastWorkbenchStationBindingDto> bindings)
                                && bindings != null)
                            {
                                for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                                {
                                    string boundAssetName = bindings[bindingIndex]?.assetName ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(boundAssetName))
                                    {
                                        boundAssetNames.Add(boundAssetName);
                                    }
                                }
                            }
                        }

                        DispatchWorkbenchStationConflictDto[] remainingConflicts = conflicts
                            .Select(conflict => conflict == null
                                ? null
                                : new DispatchWorkbenchStationConflictDto
                                {
                                    assetName = conflict.assetName ?? string.Empty,
                                    suggestedLang = conflict.suggestedLang ?? string.Empty
                                })
                            .Where(conflict => conflict != null
                                && !string.IsNullOrWhiteSpace(conflict.assetName)
                                && !boundAssetNames.Contains(conflict.assetName))
                            .ToArray();
                        group.Representative.conflictAssets = remainingConflicts;
                    }
                }

                internal void ApplyPending(
                    List<StationGroup> stationGroups,
                    string lineId)
                {
                    if (stationGroups == null || stationGroups.Count == 0)
                    {
                        return;
                    }

                    Dictionary<string, DispatchWorkbenchStationConflictDto[]> pendingConflictsByStationKey = null;
                    if (!string.IsNullOrEmpty(lineId))
                    {
                        PendingConflicts.TryGetValue(lineId, out pendingConflictsByStationKey);
                    }

                    for (int i = 0; i < stationGroups.Count; i++)
                    {
                        StationGroup group = stationGroups[i];
                        if (group?.Representative == null)
                        {
                            continue;
                        }

                        if (pendingConflictsByStationKey != null
                            && !string.IsNullOrEmpty(group.Key)
                            && pendingConflictsByStationKey.TryGetValue(group.Key, out DispatchWorkbenchStationConflictDto[] pendingConflicts)
                            && pendingConflicts != null
                            && pendingConflicts.Length > 0)
                        {
                            group.Representative.conflictAssets = pendingConflicts
                                .Select(conflict => conflict == null
                                    ? null
                                    : new DispatchWorkbenchStationConflictDto
                                    {
                                        assetName = conflict.assetName ?? string.Empty,
                                        suggestedLang = conflict.suggestedLang ?? string.Empty
                                    })
                                .Where(conflict => conflict != null && !string.IsNullOrWhiteSpace(conflict.assetName))
                                .ToArray();
                            continue;
                        }

                        group.Representative.conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>();
                    }
                }

                internal void Store(
                    string lineId,
                    List<StationGroup> stationGroups)
                {
                    if (string.IsNullOrEmpty(lineId))
                    {
                        return;
                    }

                    Dictionary<string, DispatchWorkbenchStationConflictDto[]> conflictsByStationKey =
                        new Dictionary<string, DispatchWorkbenchStationConflictDto[]>(StringComparer.Ordinal);
                    if (stationGroups != null)
                    {
                        for (int i = 0; i < stationGroups.Count; i++)
                        {
                            StationGroup group = stationGroups[i];
                            if (group?.Representative == null
                                || string.IsNullOrEmpty(group.Key)
                                || group.Representative.conflictAssets == null
                                || group.Representative.conflictAssets.Length == 0)
                            {
                                continue;
                            }

                            DispatchWorkbenchStationConflictDto[] clonedConflicts = group.Representative.conflictAssets
                                .Select(conflict => conflict == null
                                    ? null
                                    : new DispatchWorkbenchStationConflictDto
                                    {
                                        assetName = conflict.assetName ?? string.Empty,
                                        suggestedLang = conflict.suggestedLang ?? string.Empty
                                    })
                                .Where(conflict => conflict != null && !string.IsNullOrWhiteSpace(conflict.assetName))
                                .ToArray();
                            if (clonedConflicts.Length > 0)
                            {
                                conflictsByStationKey[group.Key] = clonedConflicts;
                            }
                        }
                    }

                    if (conflictsByStationKey.Count == 0)
                    {
                        PendingConflicts.Remove(lineId);
                        return;
                    }

                    PendingConflicts[lineId] = conflictsByStationKey;
                }

                internal void Clear(string lineId)
                {
                    if (!string.IsNullOrEmpty(lineId))
                    {
                        PendingConflicts.Remove(lineId);
                    }
                }

                internal void ClearOne(string lineId, string stationKey)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || string.IsNullOrEmpty(stationKey)
                        || !PendingConflicts.TryGetValue(lineId, out Dictionary<string, DispatchWorkbenchStationConflictDto[]> lineConflicts))
                    {
                        return;
                    }

                    lineConflicts.Remove(stationKey);
                    if (lineConflicts.Count == 0)
                    {
                        PendingConflicts.Remove(lineId);
                    }
                }

                internal List<DispatchWorkbenchStationConflictDto> Candidates(
                    string stationKey,
                    Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup)
                {
                    if (string.IsNullOrEmpty(stationKey))
                    {
                        return null;
                    }

                    Dictionary<string, DispatchWorkbenchStationConflictDto> candidatesByAssetName =
                        new Dictionary<string, DispatchWorkbenchStationConflictDto>(StringComparer.OrdinalIgnoreCase);

                    if (conflictLookup != null
                        && conflictLookup.TryGetValue(stationKey, out List<DispatchWorkbenchStationConflictDto> exactConflicts)
                        && exactConflicts != null)
                    {
                        for (int i = 0; i < exactConflicts.Count; i++)
                        {
                            DispatchWorkbenchStationConflictDto conflict = exactConflicts[i];
                            string assetName = conflict?.assetName ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(assetName))
                            {
                                continue;
                            }

                            candidatesByAssetName[assetName] = new DispatchWorkbenchStationConflictDto
                            {
                                assetName = assetName,
                                suggestedLang = conflict?.suggestedLang ?? string.Empty
                            };
                        }
                    }

                    for (int i = 0; i < Catalog.Count; i++)
                    {
                        string assetName = Catalog[i]?.name ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(assetName)
                            || !StationMatch(assetName, stationKey))
                        {
                            continue;
                        }

                        if (!candidatesByAssetName.ContainsKey(assetName))
                        {
                            candidatesByAssetName[assetName] = new DispatchWorkbenchStationConflictDto
                            {
                                assetName = assetName,
                                suggestedLang = string.Empty
                            };
                        }
                    }

                    if (candidatesByAssetName.Count <= 1)
                    {
                        return candidatesByAssetName.Values.ToList();
                    }

                    return candidatesByAssetName.Values
                        .OrderBy(conflict => conflict.assetName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                internal static bool StationMatch(string assetName, string stationKey)
                {
                    string normalizedStationKey = stationKey?.Trim() ?? string.Empty;
                    string normalizedAssetKey = MatchKey(assetName);
                    if (string.IsNullOrEmpty(normalizedStationKey) || string.IsNullOrEmpty(normalizedAssetKey))
                    {
                        return false;
                    }

                    if (normalizedAssetKey.IndexOf(normalizedStationKey, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }

                    string compactStationKey = Compact(normalizedStationKey);
                    string compactAssetKey = Compact(normalizedAssetKey);
                    if (string.IsNullOrEmpty(compactStationKey) || string.IsNullOrEmpty(compactAssetKey))
                    {
                        return false;
                    }

                    return compactAssetKey.IndexOf(compactStationKey, StringComparison.Ordinal) >= 0;
                }

                internal static string MatchKey(string value)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return string.Empty;
                    }

                    string source = IoPath.GetFileNameWithoutExtension(value.Trim());
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        return string.Empty;
                    }

                    StringBuilder builder = new StringBuilder(source.Length);
                    bool lastWasSeparator = false;
                    for (int i = 0; i < source.Length; i++)
                    {
                        char ch = char.ToLowerInvariant(source[i]);
                        if (char.IsLetterOrDigit(ch))
                        {
                            builder.Append(ch);
                            lastWasSeparator = false;
                            continue;
                        }

                        if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-')
                        {
                            if (!lastWasSeparator && builder.Length > 0)
                            {
                                builder.Append(' ');
                                lastWasSeparator = true;
                            }
                        }
                    }

                    return builder.ToString().Trim();
                }

                internal static string Compact(string value)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return string.Empty;
                    }

                    StringBuilder builder = new StringBuilder(value.Length);
                    for (int i = 0; i < value.Length; i++)
                    {
                        char ch = value[i];
                        if (!char.IsWhiteSpace(ch))
                        {
                            builder.Append(ch);
                        }
                    }

                    return builder.ToString();
                }
    }
}
