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
    internal sealed class Rules : ModuleBase
    {
        internal Rules(Context context) : base(context) { }

                public string SaveBroadcastRulesJson(string requestJson)
                {
                    BroadcastWorkbenchSaveRulesResult result = new BroadcastWorkbenchSaveRulesResult
                    {
                        success = false,
                        error = string.Empty
                    };

                    try
                    {
                        LoadWorkbench();
                        BroadcastWorkbenchSaveRulesRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchSaveRulesRequest>(requestJson);
                        string lineId = request?.lineId ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(lineId))
                        {
                            result.error = "Line is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        List<BroadcastWorkbenchRuleDto> normalizedRules = Normalize(request?.rules);
                        if (normalizedRules.Count == 0)
                        {
                            DraftRules.Remove(lineId);
                        }
                        else
                        {
                            DraftRules[lineId] = normalizedRules;
                        }

                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(lineId));
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("SaveBroadcastRulesJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal List<BroadcastWorkbenchRuleDto> Applied(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || !AppliedRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                        || rules == null)
                    {
                        return null;
                    }

                    return rules;
                }

                internal List<BroadcastWorkbenchRuleDto> Draft(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || !DraftRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                        || rules == null)
                    {
                        return null;
                    }

                    return rules;
                }

                internal BroadcastWorkbenchRuleDto[] DraftRows(string lineId)
                {
                    if (string.IsNullOrEmpty(lineId)
                        || !DraftRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                        || rules == null
                        || rules.Count == 0)
                    {
                        return Array.Empty<BroadcastWorkbenchRuleDto>();
                    }

                    return rules
                        .Select(Clone)
                        .Where(rule => rule != null)
                        .ToArray();
                }

                internal static void RemoveRefs(
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> allRules,
                    string assetName)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> lineEntry in allRules)
                    {
                        List<BroadcastWorkbenchRuleDto> rules = lineEntry.Value;
                        if (rules == null)
                        {
                            continue;
                        }

                        for (int i = 0; i < rules.Count; i++)
                        {
                            BroadcastWorkbenchRuleDto rule = rules[i];
                            if (rule?.nodes == null || rule.nodes.Length == 0)
                            {
                                continue;
                            }

                            rule.nodes = rule.nodes
                                .Where(node => node != null
                                    && !(string.Equals(node.type, "asset", StringComparison.Ordinal)
                                        && string.Equals(node.name, assetName, StringComparison.OrdinalIgnoreCase)))
                                .ToArray();
                        }
                    }
                }

                internal static void RemoveAllRefs(
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> allRules)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> lineEntry in allRules)
                    {
                        List<BroadcastWorkbenchRuleDto> rules = lineEntry.Value;
                        if (rules == null)
                        {
                            continue;
                        }

                        for (int i = 0; i < rules.Count; i++)
                        {
                            BroadcastWorkbenchRuleDto rule = rules[i];
                            if (rule?.nodes == null || rule.nodes.Length == 0)
                            {
                                continue;
                            }

                            rule.nodes = rule.nodes
                                .Where(node => node != null && !string.Equals(node.type, "asset", StringComparison.Ordinal))
                                .ToArray();
                        }
                    }
                }

                internal static void RestoreInto(
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> target,
                    BroadcastWorkbenchPersistedRuleState[] persistedRules)
                {
                    if (persistedRules == null)
                    {
                        return;
                    }

                    for (int i = 0; i < persistedRules.Length; i++)
                    {
                        BroadcastWorkbenchPersistedRuleState ruleState = persistedRules[i];
                        if (ruleState == null || string.IsNullOrWhiteSpace(ruleState.lineId))
                        {
                            continue;
                        }

                        List<BroadcastWorkbenchRuleDto> normalizedRules = Normalize(ruleState.rules);
                        if (normalizedRules.Count > 0)
                        {
                            target[ruleState.lineId] = normalizedRules;
                        }
                    }
                }

                internal static void Copy(
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> source,
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> target)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> entry in source)
                    {
                        if (entry.Value == null || entry.Value.Count == 0)
                        {
                            continue;
                        }

                        target[entry.Key] = entry.Value
                            .Select(Clone)
                            .Where(rule => rule != null)
                            .ToList();
                    }
                }

                internal static bool Same(
                    List<BroadcastWorkbenchRuleDto> left,
                    List<BroadcastWorkbenchRuleDto> right)
                {
                    string leftJson = global::RapidTransitMod.Workbenches.Json.Write(
                        left?.Select(Clone).Where(rule => rule != null).ToArray()
                        ?? Array.Empty<BroadcastWorkbenchRuleDto>());
                    string rightJson = global::RapidTransitMod.Workbenches.Json.Write(
                        right?.Select(Clone).Where(rule => rule != null).ToArray()
                        ?? Array.Empty<BroadcastWorkbenchRuleDto>());
                    return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
                }

                internal static BroadcastWorkbenchRuleDto Clone(BroadcastWorkbenchRuleDto rule)
                {
                    if (rule == null)
                    {
                        return null;
                    }

                    return new BroadcastWorkbenchRuleDto
                    {
                        id = rule.id ?? string.Empty,
                        title = rule.title ?? string.Empty,
                        titleKey = rule.titleKey ?? string.Empty,
                        triggerId = rule.triggerId ?? string.Empty,
                        trigger = rule.trigger ?? string.Empty,
                        triggerKey = rule.triggerKey ?? string.Empty,
                        nodes = rule.nodes == null
                            ? Array.Empty<BroadcastWorkbenchRuleNodeDto>()
                            : rule.nodes
                                .Select(CloneNode)
                                .Where(node => node != null)
                                .ToArray()
                    };
                }

                internal static BroadcastWorkbenchRuleNodeDto CloneNode(BroadcastWorkbenchRuleNodeDto node)
                {
                    if (node == null)
                    {
                        return null;
                    }

                    return new BroadcastWorkbenchRuleNodeDto
                    {
                        id = node.id ?? string.Empty,
                        type = node.type ?? string.Empty,
                        name = node.name ?? string.Empty,
                        nameKey = node.nameKey ?? string.Empty,
                        desc = node.desc ?? string.Empty,
                        descKey = node.descKey ?? string.Empty,
                        langIndex = node.langIndex > 0 ? node.langIndex : 1,
                        delaySeconds = node.delaySeconds < 0f ? 0f : node.delaySeconds
                    };
                }

                internal static List<BroadcastWorkbenchRuleDto> Normalize(IEnumerable<BroadcastWorkbenchRuleDto> rules)
                {
                    List<BroadcastWorkbenchRuleDto> normalizedRules = new List<BroadcastWorkbenchRuleDto>();
                    if (rules == null)
                    {
                        return normalizedRules;
                    }

                    foreach (BroadcastWorkbenchRuleDto rule in rules)
                    {
                        if (rule == null || string.IsNullOrWhiteSpace(rule.id))
                        {
                            continue;
                        }

                        BroadcastWorkbenchRuleDto normalizedRule = Clone(rule);
                        if (normalizedRule == null)
                        {
                            continue;
                        }

                        normalizedRule.triggerId = Trigger(normalizedRule.triggerId);
                        if (string.IsNullOrEmpty(normalizedRule.triggerId))
                        {
                            continue;
                        }

                        normalizedRule.nodes = normalizedRule.nodes
                            .Where(node => node != null
                                && !string.IsNullOrWhiteSpace(node.id)
                                && !string.IsNullOrWhiteSpace(node.type))
                            .ToArray();
                        normalizedRules.Add(normalizedRule);
                    }

                    return normalizedRules;
                }

                internal static string Trigger(string triggerId)
                {
                    switch ((triggerId ?? string.Empty).Trim())
                    {
                        case "approach_station":
                        case "stop_and_open":
                        case "leave_station":
                        case "mid_route":
                        case "bypass_waiting":
                            return triggerId.Trim();
                        default:
                            return string.Empty;
                    }
                }
    }
}
