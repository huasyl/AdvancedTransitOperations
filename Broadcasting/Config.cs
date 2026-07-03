using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Networking;
using RuntimeConfig = RapidTransitMod.Broadcasting.WorkbenchBackend.RuntimeConfig;
using StationGroup = RapidTransitMod.Broadcasting.WorkbenchBackend.StationGroup;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class Config
    {
        private readonly RuntimeConfig m_Source;

        internal Config(RuntimeConfig source)
        {
            m_Source = source;
        }

        internal bool Enabled => m_Source.Enabled;
        internal int VolumeForLine(string lineId) => m_Source.VolumeForLine(lineId);
        internal List<BroadcastWorkbenchAssetDto> Assets => m_Source.Assets;
        internal Dictionary<string, List<BroadcastWorkbenchRuleDto>> RulesByLine => m_Source.RulesByLine;
        internal Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> PlatformsByLine => m_Source.PlatformsByLine;

        internal bool EnsureLine(string lineId, Entity line, out List<StationGroup> stationGroups)
            => m_Source.EnsureLine(lineId, line, out stationGroups);

        internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> Bindings(string lineId)
            => m_Source.Bindings(lineId);

        internal List<BroadcastWorkbenchAssetDto> AssetsForLine(string lineId)
            => m_Source.AssetsForLine(lineId);

        internal BroadcastWorkbenchRuleDto CloneRule(BroadcastWorkbenchRuleDto rule)
            => m_Source.CloneRule(rule);

        internal BroadcastWorkbenchRuleNodeDto CloneNode(BroadcastWorkbenchRuleNodeDto node)
            => m_Source.CloneNode(node);

        internal string AssetPath(string filePath) => m_Source.AssetPath(filePath);

        internal AudioType AudioType(string filePath) => m_Source.AudioType(filePath);

        internal UnityWebRequest AudioRequest(string path, AudioType audioType)
            => m_Source.AudioRequest(path, audioType);

        internal int ClampVolume(int volumePercent) => m_Source.Clamp(volumePercent);

        internal string AssetCacheKey(string lineId, string assetName)
            => m_Source.AssetCacheKey(lineId, assetName);
    }
}
