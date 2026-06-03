using System;
using System.Collections.Generic;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class State
    {
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
        internal int DraftVolume = 80;
        internal int AppliedVolume = 80;
    }
}
