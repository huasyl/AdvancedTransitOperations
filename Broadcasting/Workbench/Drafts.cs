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
    internal sealed class Drafts : ModuleBase
    {
        internal Drafts(Context context) : base(context) { }

                public string ApplyBroadcastConfigJson(string requestJson)
                {
                    BroadcastWorkbenchApplyResult result = new BroadcastWorkbenchApplyResult
                    {
                        success = false,
                        error = string.Empty,
                        snapshot = null
                    };

                    try
                    {
                        LoadWorkbench();
                        BroadcastWorkbenchApplyRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchApplyRequest>(requestJson);
                        string lineId = request?.lineId ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(lineId))
                        {
                            result.error = "Line is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        Apply(lineId);
                        AppliedLines.Add(lineId);
                        AppliedVol = Preview.Clamp(DraftVol);
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        m_Announcements.ApplyVolume();

                        BroadcastWorkbenchSnapshot snapshot = m_Ctx.Snapshot.Build(lineId);
                        result.success = true;
                        result.snapshot = snapshot;
                        global::RapidTransitMod.Workbenches.UiEvents.Push(snapshot);
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("ApplyBroadcastConfigJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal void Apply(string lineId)
                {
                    if (string.IsNullOrWhiteSpace(lineId))
                    {
                        return;
                    }

                    if (DraftBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftBindings)
                        && draftBindings != null
                        && draftBindings.Count > 0)
                    {
                        AppliedBindings[lineId] = Bindings.CloneLine(draftBindings);
                    }
                    else
                    {
                        AppliedBindings.Remove(lineId);
                    }

                    if (DraftRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> draftRules)
                        && draftRules != null
                        && draftRules.Count > 0)
                    {
                        AppliedRules[lineId] = draftRules
                            .Select(Rules.Clone)
                            .Where(rule => rule != null)
                            .ToList();
                    }
                    else
                    {
                        AppliedRules.Remove(lineId);
                    }

                    if (DraftPlatforms.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftAnnouncements)
                        && draftAnnouncements != null
                        && draftAnnouncements.Count > 0)
                    {
                        AppliedPlatforms[lineId] = Platforms.CloneLine(draftAnnouncements);
                    }
                    else
                    {
                        AppliedPlatforms.Remove(lineId);
                    }
                }

                internal bool Dirty(string lineId)
                {
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> appliedBindings = m_Ctx.Bindings.Applied(lineId);
                    Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftBindings = m_Ctx.Bindings.Draft(lineId);
                    if (!Bindings.Same(appliedBindings, draftBindings))
                    {
                        return true;
                    }

                    List<BroadcastWorkbenchRuleDto> appliedRules = m_Ctx.Rules.Applied(lineId);
                    List<BroadcastWorkbenchRuleDto> draftRules = m_Ctx.Rules.Draft(lineId);
                    if (!Rules.Same(appliedRules, draftRules))
                    {
                        return true;
                    }

                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> appliedAnnouncements =
                        m_Ctx.Platforms.Applied(lineId);
                    Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftAnnouncements =
                        m_Ctx.Platforms.Draft(lineId);
                    if (!Platforms.Same(appliedAnnouncements, draftAnnouncements))
                    {
                        return true;
                    }

                    return false;
                }

                internal bool EnsureLine(
                    string lineId,
                    Entity line,
                    out List<StationGroup> stationGroups)
                {
                    stationGroups = m_Ctx.Snapshot.Groups(line);
                    return false;
                }
    }
}
