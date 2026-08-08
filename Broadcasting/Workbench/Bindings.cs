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
    internal sealed class Bindings : ModuleBase
    {
        internal Bindings(Context context) : base(context) { }

                public string LoadBroadcastBindingSlotHintsJson(string requestJson)
                {
                    BroadcastWorkbenchBindingSlotHintsResult result = new BroadcastWorkbenchBindingSlotHintsResult
                    {
                        success = false,
                        error = string.Empty,
                        slotHints = Array.Empty<BroadcastWorkbenchBindingSlotHintDto>()
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "loadBroadcastBindingSlotHints");
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

                        result.success = true;
                        result.slotHints = Hints(resolvedLineId);
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("LoadBroadcastBindingSlotHintsJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string SaveBroadcastStationBindingJson(string requestJson)
                {
                    BroadcastWorkbenchSaveStationBindingResult result = new BroadcastWorkbenchSaveStationBindingResult
                    {
                        success = false,
                        error = string.Empty
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "saveBroadcastStationBinding");
                        using (UseScope(scope))
                        {
                        BroadcastWorkbenchSaveStationBindingRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchSaveStationBindingRequest>(requestJson);
                        string lineId = scope.NormalizeLineId(request?.lineId);
                        string stationId = request?.stationId ?? string.Empty;
                        string assetName = request?.assetName ?? string.Empty;
                        if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(stationId))
                        {
                            result.error = "Line or station is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }
                        if (!scope.MatchesLineId(lineId))
                        {
                            result.error = "Line does not belong to mode " + scope.Token + ".";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        if (!string.IsNullOrEmpty(assetName)
                            && !m_Ctx.Assets.HasUsableAsset(assetName))
                        {
                            result.error = m_Ctx.Assets.HasCatalogAsset(assetName)
                                ? "Selected asset file was not found."
                                : "Selected asset was not found.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        BroadcastWorkbenchStationBindingDto[] nextBindings = string.IsNullOrEmpty(assetName)
                            ? Array.Empty<BroadcastWorkbenchStationBindingDto>()
                            : new[]
                            {
                                new BroadcastWorkbenchStationBindingDto
                                {
                                    stationId = stationId,
                                    langIndex = 1,
                                    assetName = assetName
                                }
                            };

                        Save(lineId, stationId, nextBindings);
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, lineId));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("SaveBroadcastStationBindingJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string SaveBroadcastStationBindingsJson(string requestJson)
                {
                    BroadcastWorkbenchSaveStationBindingResult result = new BroadcastWorkbenchSaveStationBindingResult
                    {
                        success = false,
                        error = string.Empty
                    };

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "saveBroadcastStationBindings");
                        using (UseScope(scope))
                        {
                        BroadcastWorkbenchSaveStationBindingsRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchSaveStationBindingsRequest>(requestJson);
                        string lineId = scope.NormalizeLineId(request?.lineId);
                        string stationId = request?.stationId ?? string.Empty;
                        if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(stationId))
                        {
                            result.error = "Line or station is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }
                        if (!scope.MatchesLineId(lineId))
                        {
                            result.error = "Line does not belong to mode " + scope.Token + ".";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        Validate(request?.bindings);
                        Save(lineId, stationId, request?.bindings);
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, lineId));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("SaveBroadcastStationBindingsJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal void Save(
                    string lineId,
                    string stationId,
                    IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
                {
                    List<BroadcastWorkbenchStationBindingDto> normalizedBindings =
                        Normalize(stationId, bindings);
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                        EnsureDraft(lineId);
                    m_Ctx.Conflicts.ClearOne(lineId, stationId);
                    if (normalizedBindings.Count == 0)
                    {
                        lineBindings.Remove(stationId);
                    }
                    else
                    {
                        lineBindings[stationId] = Clone(stationId, normalizedBindings);
                    }

                    if (lineBindings.Count == 0)
                    {
                        DraftBindings.Remove(lineId);
                    }
                }

                internal void Validate(IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
                {
                    if (bindings == null)
                    {
                        return;
                    }

                    foreach (BroadcastWorkbenchStationBindingDto binding in bindings)
                    {
                        string assetName = binding?.assetName ?? string.Empty;
                        if (string.IsNullOrEmpty(assetName))
                        {
                            continue;
                        }

                        if (!m_Ctx.Assets.HasUsableAsset(assetName))
                        {
                            throw new InvalidOperationException(
                                m_Ctx.Assets.HasCatalogAsset(assetName)
                                    ? "Selected asset file was not found."
                                    : "Selected asset was not found.");
                        }
                    }
                }

                internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> EnsureDraft(string lineId)
                {
                    string lineKey = lineId ?? string.Empty;
                    if (!DraftBindings.TryGetValue(lineKey, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings))
                    {
                        lineBindings = new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                        DraftBindings[lineKey] = lineBindings;
                    }

                    return lineBindings;
                }

                internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> Applied(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || !AppliedBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                        || lineBindings == null)
                    {
                        return null;
                    }

                    return lineBindings;
                }

                internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> Draft(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId))
                    {
                        return null;
                    }

                    if (DraftBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                        && lineBindings != null)
                    {
                        return lineBindings;
                    }

                    return Applied(lineId);
                }

                internal static List<BroadcastWorkbenchStationBindingDto> Normalize(
                    string stationId,
                    IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
                {
                    List<BroadcastWorkbenchStationBindingDto> normalized = new List<BroadcastWorkbenchStationBindingDto>();
                    if (bindings == null)
                    {
                        return normalized;
                    }

                    int nextIndex = 1;
                    foreach (BroadcastWorkbenchStationBindingDto binding in bindings)
                    {
                        string assetName = binding?.assetName?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(assetName))
                        {
                            continue;
                        }

                        normalized.Add(new BroadcastWorkbenchStationBindingDto
                        {
                            stationId = stationId ?? binding?.stationId ?? string.Empty,
                            lang = Label(binding?.lang, nextIndex),
                            langIndex = nextIndex,
                            assetName = assetName
                        });
                        nextIndex++;
                    }

                    return normalized;
                }

                internal static List<BroadcastWorkbenchStationBindingDto> Clone(
                    string stationId,
                    IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
                {
                    return Normalize(stationId, bindings);
                }

                internal static Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> CloneLine(
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> source)
                {
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> clone =
                        new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                    if (source == null)
                    {
                        return clone;
                    }

                    foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> entry in source)
                    {
                        List<BroadcastWorkbenchStationBindingDto> bindings = Clone(entry.Key, entry.Value);
                        if (bindings.Count > 0)
                        {
                            clone[entry.Key] = bindings;
                        }
                    }

                    return clone;
                }

                internal static string Label(string lang, int langIndex)
                {
                    string normalized = (lang ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(normalized))
                    {
                        return string.Empty;
                    }

                    return string.Equals(normalized, langIndex.ToString(), StringComparison.Ordinal)
                        ? string.Empty
                        : normalized;
                }

                internal static BroadcastWorkbenchStationBindingDto[] Flatten(
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                {
                    if (lineBindings == null || lineBindings.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchStationBindingDto>();
                    }

                    return lineBindings
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .SelectMany(entry => Clone(entry.Key, entry.Value))
                        .ToArray();
                }

                internal static bool TryPrimary(
                    List<BroadcastWorkbenchStationBindingDto> bindings,
                    out string assetName)
                {
                    assetName = bindings?
                        .OrderBy(binding => binding?.langIndex ?? int.MaxValue)
                        .Select(binding => binding?.assetName ?? string.Empty)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                        ?? string.Empty;
                    return !string.IsNullOrEmpty(assetName);
                }

                internal BroadcastWorkbenchBindingSlotHintDto[] Hints(string lineId)
                {
                    Dictionary<int, HashSet<string>> labelsBySlot =
                        new Dictionary<int, HashSet<string>>();
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                        Draft(lineId);
                    if (lineBindings == null || lineBindings.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchBindingSlotHintDto>();
                    }

                    foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> stationEntry in lineBindings)
                    {
                        List<BroadcastWorkbenchStationBindingDto> bindings = stationEntry.Value;
                        if (bindings == null || bindings.Count == 0)
                        {
                            continue;
                        }

                        int compactSlotIndex = 1;
                        foreach (BroadcastWorkbenchStationBindingDto binding in bindings
                            .Where(binding => binding != null && !string.IsNullOrWhiteSpace(binding.assetName))
                            .OrderBy(binding => binding.langIndex > 0 ? binding.langIndex : int.MaxValue))
                        {
                            string label = (binding.lang ?? string.Empty).Trim();
                            if (string.IsNullOrEmpty(label))
                            {
                                compactSlotIndex++;
                                continue;
                            }

                            if (!labelsBySlot.TryGetValue(compactSlotIndex, out HashSet<string> labels))
                            {
                                labels = new HashSet<string>(StringComparer.Ordinal);
                                labelsBySlot[compactSlotIndex] = labels;
                            }

                            labels.Add(label);
                            compactSlotIndex++;
                        }
                    }

                    return labelsBySlot
                        .OrderBy(entry => entry.Key)
                        .Select(entry => new BroadcastWorkbenchBindingSlotHintDto
                        {
                            langIndex = entry.Key,
                            labels = entry.Value
                                .OrderBy(label => label, StringComparer.Ordinal)
                                .ToArray()
                        })
                        .ToArray();
                }

                internal BroadcastWorkbenchStationBindingDto[] DraftRows(
                    string lineId,
                    List<StationGroup> stationGroups)
                {
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                        Draft(lineId);
                    if (string.IsNullOrEmpty(lineId)
                        || lineBindings == null
                        || lineBindings.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchStationBindingDto>();
                    }

                    if (stationGroups == null || stationGroups.Count == 0)
                    {
                        return lineBindings
                            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .SelectMany(entry => Clone(entry.Key, entry.Value))
                            .ToArray();
                    }

                    HashSet<string> validStationIds = new HashSet<string>(
                        stationGroups
                            .Select(group => group?.Representative?.id ?? string.Empty)
                            .Where(id => !string.IsNullOrWhiteSpace(id)),
                        StringComparer.Ordinal);
                    return lineBindings
                        .Where(entry => validStationIds.Contains(entry.Key))
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .SelectMany(entry => Clone(entry.Key, entry.Value))
                        .ToArray();
                }

                internal static void RestoreInto(
                    Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> target,
                    BroadcastWorkbenchPersistedLineBindingState[] persistedLineBindings)
                {
                    if (persistedLineBindings == null)
                    {
                        return;
                    }

                    for (int i = 0; i < persistedLineBindings.Length; i++)
                    {
                        BroadcastWorkbenchPersistedLineBindingState lineBinding = persistedLineBindings[i];
                        if (lineBinding == null || string.IsNullOrWhiteSpace(lineBinding.lineId) || lineBinding.stationBindings == null)
                        {
                            continue;
                        }

                        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                            new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                        for (int j = 0; j < lineBinding.stationBindings.Length; j++)
                        {
                            BroadcastWorkbenchStationBindingDto stationBinding = lineBinding.stationBindings[j];
                            if (stationBinding == null
                                || string.IsNullOrWhiteSpace(stationBinding.stationId)
                                || string.IsNullOrWhiteSpace(stationBinding.assetName))
                            {
                                continue;
                            }

                            if (!lineBindings.TryGetValue(stationBinding.stationId, out List<BroadcastWorkbenchStationBindingDto> stationBindings))
                            {
                                stationBindings = new List<BroadcastWorkbenchStationBindingDto>();
                                lineBindings[stationBinding.stationId] = stationBindings;
                            }

                            stationBindings.Add(stationBinding);
                        }

                        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> normalizedByStationId = null;
                        List<string> emptyStationIds = null;
                        foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> stationEntry in lineBindings)
                        {
                            List<BroadcastWorkbenchStationBindingDto> normalizedBindings =
                                Normalize(stationEntry.Key, stationEntry.Value);
                            if (normalizedBindings.Count == 0)
                            {
                                emptyStationIds ??= new List<string>();
                                emptyStationIds.Add(stationEntry.Key);
                                continue;
                            }

                            normalizedByStationId ??= new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                            normalizedByStationId[stationEntry.Key] = normalizedBindings;
                        }

                        if (normalizedByStationId != null)
                        {
                            foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> normalizedEntry in normalizedByStationId)
                            {
                                lineBindings[normalizedEntry.Key] = normalizedEntry.Value;
                            }
                        }

                        if (emptyStationIds != null)
                        {
                            for (int stationIndex = 0; stationIndex < emptyStationIds.Count; stationIndex++)
                            {
                                lineBindings.Remove(emptyStationIds[stationIndex]);
                            }
                        }

                        if (lineBindings.Count > 0)
                        {
                            target[lineBinding.lineId] = lineBindings;
                        }
                    }
                }

                internal static void Copy(
                    Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source,
                    Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> target)
                {
                    foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> entry in source)
                    {
                        if (entry.Value == null || entry.Value.Count == 0)
                        {
                            continue;
                        }

                        target[entry.Key] = CloneLine(entry.Value);
                    }
                }

                internal static bool Same(
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> left,
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> right)
                {
                    string leftJson = global::RapidTransitMod.Workbenches.Json.Write(Flatten(left));
                    string rightJson = global::RapidTransitMod.Workbenches.Json.Write(Flatten(right));
                    return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
                }
    }
}
