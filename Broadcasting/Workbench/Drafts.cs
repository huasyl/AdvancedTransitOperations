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
