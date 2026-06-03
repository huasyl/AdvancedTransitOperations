using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Networking;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class RuntimeConfig
    {
        private readonly Context m_Context;

        internal RuntimeConfig(Context context) => m_Context = context;

        internal bool Enabled => m_Context.WorkbenchAccess.Enabled;
        internal int Volume => m_Context.State.AppliedVolume;
        internal List<BroadcastWorkbenchAssetDto> Assets => m_Context.State.Catalog;
        internal Dictionary<string, List<BroadcastWorkbenchRuleDto>> RulesByLine => m_Context.State.AppliedRules;
        internal Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> PlatformsByLine => m_Context.State.AppliedPlatforms;

        internal bool EnsureLine(string lineId, Entity line, out List<StationGroup> stationGroups)
            => m_Context.Drafts.EnsureLine(lineId, line, out stationGroups);

        internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> Bindings(string lineId)
            => m_Context.Bindings.Applied(lineId);

        internal BroadcastWorkbenchRuleDto CloneRule(BroadcastWorkbenchRuleDto rule)
            => Rules.Clone(rule);

        internal BroadcastWorkbenchRuleNodeDto CloneNode(BroadcastWorkbenchRuleNodeDto node)
            => Rules.CloneNode(node);

        internal string AssetPath(string filePath)
            => RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Path(filePath);

        internal AudioType AudioType(string filePath) => Preview.AudioType(filePath);

        internal UnityWebRequest AudioRequest(string path, AudioType audioType)
            => Preview.Request(path, audioType);

        internal int Clamp(int volumePercent) => Preview.Clamp(volumePercent);
    }
}
