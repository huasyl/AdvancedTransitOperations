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
        private readonly Dictionary<string, LineFlags> m_LineFlags =
            new Dictionary<string, LineFlags>(System.StringComparer.Ordinal);

        internal readonly struct LineFlags
        {
            internal readonly bool HasVehicle;
            internal readonly bool HasIdle;
            internal readonly bool HasApproach;

            internal LineFlags(bool hasVehicle, bool hasIdle, bool hasApproach)
            {
                HasVehicle = hasVehicle;
                HasIdle = hasIdle;
                HasApproach = hasApproach;
            }

            internal bool HasPlatform => HasIdle || HasApproach;
            internal bool Any => HasVehicle || HasPlatform;
        }

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

        internal LineFlags Flags(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return default;
            }

            if (m_LineFlags.TryGetValue(lineId, out LineFlags cached))
            {
                return cached;
            }

            bool hasVehicle = RulesByLine.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                && rules != null
                && rules.Exists(rule => rule != null && rule.nodes != null && rule.nodes.Length > 0);
            bool hasIdle = false;
            bool hasApproach = false;
            if (PlatformsByLine.TryGetValue(
                    lineId,
                    out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements)
                && announcements != null)
            {
                foreach (BroadcastWorkbenchPlatformAnnouncementDto announcement in announcements.Values)
                {
                    if (announcement == null
                        || !announcement.enabled
                        || announcement.nodes == null
                        || announcement.nodes.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(
                            announcement.triggerId,
                            TriggerConstants.PlatformIdleTriggerId,
                            System.StringComparison.Ordinal))
                    {
                        hasIdle = true;
                    }
                    else if (string.Equals(
                            announcement.triggerId,
                            TriggerConstants.PlatformApproachTriggerId,
                            System.StringComparison.Ordinal))
                    {
                        hasApproach = true;
                    }

                    if (hasIdle && hasApproach)
                    {
                        break;
                    }
                }
            }

            LineFlags flags = new LineFlags(hasVehicle, hasIdle, hasApproach);
            m_LineFlags[lineId] = flags;
            return flags;
        }

        internal void ClearFlags() => m_LineFlags.Clear();
    }
}
