using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Apply : ModuleBase
    {
        internal Apply(Context context) : base(context) { }

        internal PreparedApply Prepare(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "applyBroadcastConfig");
            ApplyRequest request =
                global::RapidTransitMod.Workbenches.Json.Read<ApplyRequest>(requestJson ?? string.Empty);
            if (request != null)
            {
                request.mode = scope.Token;
            }
            ApplyLineConfig[] requestLines = request?.lines ?? Array.Empty<ApplyLineConfig>();
            List<PreparedLine> preparedLines = new List<PreparedLine>();
            HashSet<string> seenLineIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < requestLines.Length; i++)
            {
                ApplyLineConfig line = requestLines[i];
                string lineId = scope.NormalizeLineId(line?.lineId);
                if (string.IsNullOrWhiteSpace(lineId))
                {
                    throw new InvalidOperationException("Line is missing.");
                }
                if (!scope.MatchesLineId(lineId))
                {
                    throw new InvalidOperationException("Line does not belong to mode " + scope.Token + ".");
                }

                if (!seenLineIds.Add(lineId))
                {
                    throw new InvalidOperationException("Duplicate line apply payload was provided.");
                }

                preparedLines.Add(new PreparedLine
                {
                    LineId = lineId,
                    StationBindings = line?.stationBindings ?? Array.Empty<BroadcastWorkbenchStationBindingDto>(),
                    Rules = line?.rules ?? Array.Empty<BroadcastWorkbenchRuleDto>(),
                    PlatformAnnouncements = line?.platformAnnouncements ?? Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>()
                });
            }

            bool volumeDirty = request?.volumeDirty == true;
            int volume = Preview.Clamp(request?.volume ?? m_State.GetAppliedVolume(scope));
            if (volumeDirty && request?.volume == null)
            {
                throw new InvalidOperationException("Broadcast volume is missing.");
            }

            if (preparedLines.Count == 0 && !volumeDirty)
            {
                throw new InvalidOperationException("No broadcast changes were provided.");
            }

            return new PreparedApply(scope, preparedLines, volumeDirty, volume);
        }

        internal ApplyResult Commit(PreparedApply prepared)
        {
            if (prepared == null)
            {
                throw new InvalidOperationException("Broadcast apply payload is missing.");
            }

            LoadWorkbench();
            ApplyStateSnapshot rollback = CaptureState();

            try
            {
                using (UseScope(prepared.Scope))
                {
                List<WorkbenchLineRuntime> runtimeLines = Lines();
                Dictionary<string, PreparedLineCommit> preparedCommits =
                    new Dictionary<string, PreparedLineCommit>(StringComparer.Ordinal);
                List<string> warnings = new List<string>();

                foreach (PreparedLine line in prepared.Lines)
                {
                    WorkbenchLineRuntime runtime = FindLine(runtimeLines, line.LineId);
                    if (runtime == null)
                {
                    throw new InvalidOperationException("Line was not found.");
                }

                    HashSet<string> validStationIds = ValidStationIds(m_Ctx.Snapshot.Groups(runtime.Entity));
                    m_Ctx.Bindings.Validate(line.StationBindings);
                    PreparedLineCommit preparedLine = PrepareLine(line, validStationIds);
                    preparedCommits[line.LineId] = preparedLine;
                    warnings.AddRange(m_Ctx.Snapshot.Warnings(line.LineId));
                }

                foreach (KeyValuePair<string, PreparedLineCommit> entry in preparedCommits)
                {
                    CommitLine(entry.Key, entry.Value);
                    AppliedLines.Add(entry.Key);
                }

                bool volumeApplied = false;
                if (prepared.VolumeDirty)
                {
                    AppliedVol = Preview.Clamp(prepared.Volume);
                    DraftVol = AppliedVol;
                    m_Ctx.Preview.ApplyVolume();
                    m_Announcements.ApplyVolume();
                    volumeApplied = true;
                }

                IncrementWorkbenchSnapshotVersion();
                SaveWorkbench();

                return new ApplyResult
                {
                    mode = prepared.Scope.Token,
                    success = true,
                    error = string.Empty,
                    version = m_WorkbenchSnapshotVersion.ToString(),
                    appliedLineIds = preparedCommits.Keys.OrderBy(lineId => lineId, StringComparer.Ordinal).ToArray(),
                    volumeApplied = volumeApplied,
                    warnings = warnings
                        .Where(warning => !string.IsNullOrWhiteSpace(warning))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
                }
            }
            catch
            {
                RestoreState(rollback);
                throw;
            }
        }

        private PreparedLineCommit PrepareLine(PreparedLine line, HashSet<string> validStationIds)
        {
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> bindings =
                new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
            foreach (IGrouping<string, BroadcastWorkbenchStationBindingDto> group in (line.StationBindings ?? Array.Empty<BroadcastWorkbenchStationBindingDto>())
                .Where(binding => binding != null && !string.IsNullOrWhiteSpace(binding.stationId))
                .GroupBy(binding => binding.stationId, StringComparer.Ordinal))
            {
                ValidateStationId(group.Key, validStationIds);
                List<BroadcastWorkbenchStationBindingDto> normalizedBindings = Bindings.Normalize(group.Key, group);
                if (normalizedBindings.Count > 0)
                {
                    bindings[group.Key] = normalizedBindings;
                }
            }

            List<BroadcastWorkbenchRuleDto> rules = Rules.Normalize(line.Rules);
            ValidateRuleAssetNodes(rules.SelectMany(rule => rule?.nodes ?? Array.Empty<BroadcastWorkbenchRuleNodeDto>()));
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> platforms =
                new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);

            BroadcastWorkbenchPlatformAnnouncementDto[] announcements = line.PlatformAnnouncements ?? Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
            for (int i = 0; i < announcements.Length; i++)
            {
                BroadcastWorkbenchPlatformAnnouncementDto source = announcements[i];
                if (source == null || string.IsNullOrWhiteSpace(source.stationId))
                {
                    continue;
                }

                ValidateStationId(source.stationId, validStationIds);
                BroadcastWorkbenchPlatformAnnouncementDto normalized = Platforms.Normalize(
                    line.LineId,
                    source.stationId,
                    source.stationName,
                    source.title,
                    source.uiTriggerId,
                    source.enabled,
                    source.nodes);
                ValidateRuleAssetNodes(normalized.nodes);
                platforms[Platforms.Key(normalized.stationId, normalized.uiTriggerId)] = normalized;
            }

            return new PreparedLineCommit(bindings, rules, platforms);
        }

        private static HashSet<string> ValidStationIds(List<StationGroup> stationGroups)
        {
            return new HashSet<string>(
                (stationGroups ?? new List<StationGroup>())
                    .Select(group => group?.Representative?.id ?? string.Empty)
                    .Where(stationId => !string.IsNullOrWhiteSpace(stationId)),
                StringComparer.Ordinal);
        }

        private static void ValidateStationId(string stationId, HashSet<string> validStationIds)
        {
            if (string.IsNullOrWhiteSpace(stationId))
            {
                return;
            }

            if (validStationIds == null || !validStationIds.Contains(stationId))
            {
                throw new InvalidOperationException("Station was not found.");
            }
        }

        private void ValidateRuleAssetNodes(IEnumerable<BroadcastWorkbenchRuleNodeDto> nodes)
        {
            if (nodes == null)
            {
                return;
            }

            foreach (BroadcastWorkbenchRuleNodeDto node in nodes)
            {
                if (node == null || !string.Equals(node.type, "asset", StringComparison.Ordinal))
                {
                    continue;
                }

                string assetName = node.name ?? string.Empty;
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

        private void CommitLine(string lineId, PreparedLineCommit prepared)
        {
            if (prepared.Bindings.Count == 0)
            {
                AppliedBindings.Remove(lineId);
            }
            else
            {
                AppliedBindings[lineId] = Bindings.CloneLine(prepared.Bindings);
            }

            if (prepared.Rules.Count == 0)
            {
                AppliedRules.Remove(lineId);
            }
            else
            {
                AppliedRules[lineId] = prepared.Rules
                    .Select(Rules.Clone)
                    .Where(rule => rule != null)
                    .ToList();
            }

            if (prepared.PlatformAnnouncements.Count == 0)
            {
                AppliedPlatforms.Remove(lineId);
            }
            else
            {
                AppliedPlatforms[lineId] = Platforms.CloneLine(prepared.PlatformAnnouncements);
            }

            DraftBindings.Remove(lineId);
            DraftRules.Remove(lineId);
            DraftPlatforms.Remove(lineId);
            m_Ctx.Conflicts.Clear(lineId);
        }

        private ApplyStateSnapshot CaptureState()
        {
            return new ApplyStateSnapshot(
                CloneBindings(AppliedBindings),
                CloneRules(AppliedRules),
                ClonePlatforms(AppliedPlatforms),
                CloneBindings(DraftBindings),
                CloneRules(DraftRules),
                ClonePlatforms(DraftPlatforms),
                new HashSet<string>(AppliedLines, StringComparer.Ordinal),
                CloneVolumes(m_State.DraftVolumesByMode),
                CloneVolumes(m_State.AppliedVolumesByMode));
        }

        private void RestoreState(ApplyStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            RestoreBindings(AppliedBindings, snapshot.AppliedBindings);
            RestoreRules(AppliedRules, snapshot.AppliedRules);
            RestorePlatforms(AppliedPlatforms, snapshot.AppliedPlatforms);
            RestoreBindings(DraftBindings, snapshot.DraftBindings);
            RestoreRules(DraftRules, snapshot.DraftRules);
            RestorePlatforms(DraftPlatforms, snapshot.DraftPlatforms);

            AppliedLines.Clear();
            foreach (string lineId in snapshot.AppliedLines)
            {
                AppliedLines.Add(lineId);
            }

            RestoreVolumes(m_State.DraftVolumesByMode, snapshot.DraftVolumesByMode);
            RestoreVolumes(m_State.AppliedVolumesByMode, snapshot.AppliedVolumesByMode);
            m_Ctx.Preview.ApplyVolume();
            m_Announcements.ApplyVolume();
        }

        private static Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> CloneBindings(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source)
        {
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> clone =
                new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> entry in source)
            {
                clone[entry.Key] = Bindings.CloneLine(entry.Value);
            }

            return clone;
        }

        private static Dictionary<string, List<BroadcastWorkbenchRuleDto>> CloneRules(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source)
        {
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> clone =
                new Dictionary<string, List<BroadcastWorkbenchRuleDto>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> entry in source)
            {
                clone[entry.Key] = (entry.Value ?? new List<BroadcastWorkbenchRuleDto>())
                    .Select(Rules.Clone)
                    .Where(rule => rule != null)
                    .ToList();
            }

            return clone;
        }

        private static Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> ClonePlatforms(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source)
        {
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> clone =
                new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> entry in source)
            {
                clone[entry.Key] = Platforms.CloneLine(entry.Value);
            }

            return clone;
        }

        private static void RestoreBindings(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> target,
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> entry in source)
            {
                target[entry.Key] = Bindings.CloneLine(entry.Value);
            }
        }

        private static void RestoreRules(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> target,
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> entry in source)
            {
                target[entry.Key] = (entry.Value ?? new List<BroadcastWorkbenchRuleDto>())
                    .Select(Rules.Clone)
                    .Where(rule => rule != null)
                    .ToList();
            }
        }

        private static void RestorePlatforms(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> target,
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> entry in source)
            {
                target[entry.Key] = Platforms.CloneLine(entry.Value);
            }
        }

        private static Dictionary<string, int> CloneVolumes(Dictionary<string, int> source)
        {
            return new Dictionary<string, int>(source ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        }

        private static void RestoreVolumes(Dictionary<string, int> target, Dictionary<string, int> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, int> entry in source ?? new Dictionary<string, int>())
            {
                target[entry.Key] = entry.Value;
            }
        }

        internal sealed class PreparedApply
        {
            internal PreparedApply(ModeScope scope, List<PreparedLine> lines, bool volumeDirty, int volume)
            {
                Scope = scope;
                Lines = lines ?? new List<PreparedLine>();
                VolumeDirty = volumeDirty;
                Volume = volume;
            }

            internal ModeScope Scope { get; }
            internal List<PreparedLine> Lines { get; }
            internal bool VolumeDirty { get; }
            internal int Volume { get; }
        }

        internal sealed class PreparedLine
        {
            internal string LineId = string.Empty;
            internal BroadcastWorkbenchStationBindingDto[] StationBindings = Array.Empty<BroadcastWorkbenchStationBindingDto>();
            internal BroadcastWorkbenchRuleDto[] Rules = Array.Empty<BroadcastWorkbenchRuleDto>();
            internal BroadcastWorkbenchPlatformAnnouncementDto[] PlatformAnnouncements = Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
        }

        private sealed class PreparedLineCommit
        {
            internal PreparedLineCommit(
                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> bindings,
                List<BroadcastWorkbenchRuleDto> rules,
                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> platformAnnouncements)
            {
                Bindings = bindings ?? new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                Rules = rules ?? new List<BroadcastWorkbenchRuleDto>();
                PlatformAnnouncements = platformAnnouncements ?? new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
            }

            internal Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> Bindings { get; }
            internal List<BroadcastWorkbenchRuleDto> Rules { get; }
            internal Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> PlatformAnnouncements { get; }
        }

        private sealed class ApplyStateSnapshot
        {
            internal ApplyStateSnapshot(
                Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> appliedBindings,
                Dictionary<string, List<BroadcastWorkbenchRuleDto>> appliedRules,
                Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> appliedPlatforms,
                Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> draftBindings,
                Dictionary<string, List<BroadcastWorkbenchRuleDto>> draftRules,
                Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> draftPlatforms,
                HashSet<string> appliedLines,
                Dictionary<string, int> draftVolumesByMode,
                Dictionary<string, int> appliedVolumesByMode)
            {
                AppliedBindings = appliedBindings;
                AppliedRules = appliedRules;
                AppliedPlatforms = appliedPlatforms;
                DraftBindings = draftBindings;
                DraftRules = draftRules;
                DraftPlatforms = draftPlatforms;
                AppliedLines = appliedLines;
                DraftVolumesByMode = draftVolumesByMode;
                AppliedVolumesByMode = appliedVolumesByMode;
            }

            internal Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> AppliedBindings { get; }
            internal Dictionary<string, List<BroadcastWorkbenchRuleDto>> AppliedRules { get; }
            internal Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> AppliedPlatforms { get; }
            internal Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> DraftBindings { get; }
            internal Dictionary<string, List<BroadcastWorkbenchRuleDto>> DraftRules { get; }
            internal Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> DraftPlatforms { get; }
            internal HashSet<string> AppliedLines { get; }
            internal Dictionary<string, int> DraftVolumesByMode { get; }
            internal Dictionary<string, int> AppliedVolumesByMode { get; }
        }
    }
}
