using System;
using System.Collections.Generic;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class State
    {
        internal readonly Dictionary<string, BroadcastWorkbenchAssetState> AssetsByMode =
            new Dictionary<string, BroadcastWorkbenchAssetState>(StringComparer.Ordinal);

        internal readonly List<BroadcastWorkbenchAssetDto> Catalog = new List<BroadcastWorkbenchAssetDto>();
        internal string AssetDir = string.Empty;
        internal string BrowseDir = string.Empty;

        internal readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> DraftBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, List<BroadcastWorkbenchRuleDto>> DraftRules =
            new Dictionary<string, List<BroadcastWorkbenchRuleDto>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> DraftPlatforms =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> AppliedBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, List<BroadcastWorkbenchRuleDto>> AppliedRules =
            new Dictionary<string, List<BroadcastWorkbenchRuleDto>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> AppliedPlatforms =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);

        internal readonly Dictionary<string, Dictionary<string, DispatchWorkbenchStationConflictDto[]>> PendingConflicts =
            new Dictionary<string, Dictionary<string, DispatchWorkbenchStationConflictDto[]>>(StringComparer.Ordinal);

        internal readonly HashSet<string> AppliedLines = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> DraftVolumesByMode =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> AppliedVolumesByMode =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal Dictionary<string, Dictionary<string, string>> CaptureAssets()
        {
            var modes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var mode in AssetsByMode)
            {
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (BroadcastWorkbenchAssetDto asset in mode.Value.Catalog)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.name)) continue;
                    names[asset.name] = !string.IsNullOrEmpty(asset.assetId) ? asset.assetId
                        : AssetStore.LegacyPath(!string.IsNullOrEmpty(asset.path) ? asset.path
                            : System.IO.Path.Combine(mode.Value.AssetDir, asset.name));
                }
                if (names.Count != 0) modes[mode.Key] = names;
            }
            return modes;
        }

        internal BroadcastWorkbenchAssetState AssetState(ModeScope scope)
        {
            string token = scope.Token;
            if (!AssetsByMode.TryGetValue(token, out BroadcastWorkbenchAssetState state) || state == null)
            {
                state = new BroadcastWorkbenchAssetState();
                AssetsByMode[token] = state;
            }

            return state;
        }

        internal int GetDraftVolume(ModeScope scope)
        {
            string token = string.IsNullOrEmpty(scope.Token) ? ModeScope.DefaultWorkbench.Token : scope.Token;
            return DraftVolumesByMode.TryGetValue(token, out int volume) ? volume : 80;
        }

        internal void SetDraftVolume(ModeScope scope, int volume)
        {
            string token = string.IsNullOrEmpty(scope.Token) ? ModeScope.DefaultWorkbench.Token : scope.Token;
            DraftVolumesByMode[token] = volume;
        }

        internal int GetAppliedVolume(ModeScope scope)
        {
            string token = string.IsNullOrEmpty(scope.Token) ? ModeScope.DefaultWorkbench.Token : scope.Token;
            return AppliedVolumesByMode.TryGetValue(token, out int volume) ? volume : 80;
        }

        internal void SetAppliedVolume(ModeScope scope, int volume)
        {
            string token = string.IsNullOrEmpty(scope.Token) ? ModeScope.DefaultWorkbench.Token : scope.Token;
            AppliedVolumesByMode[token] = volume;
        }
    }

    internal sealed class BroadcastWorkbenchAssetState
    {
        internal readonly List<BroadcastWorkbenchAssetDto> Catalog = new List<BroadcastWorkbenchAssetDto>();
        internal string AssetDir = string.Empty;
        internal string BrowseDir = string.Empty;
    }
}
