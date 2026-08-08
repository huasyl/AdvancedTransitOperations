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
using IoPath = System.IO.Path;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Persistence : ModuleBase
    {
        private static readonly ModeScope[] s_PersistedAssetScopes =
        {
            new ModeScope(TransitMode.Train),
            new ModeScope(TransitMode.Subway),
            new ModeScope(TransitMode.Tram),
            new ModeScope(TransitMode.Bus)
        };

        internal Persistence(Context context) : base(context) { }

        internal void Build(DispatchWorkbenchPersistentState persisted)
        {
            if (persisted == null)
            {
                return;
            }

            persisted.broadcastAssetDirectory = m_State.AssetState(ModeScope.DefaultWorkbench).AssetDir;
            persisted.broadcastAssets = Assets();
            persisted.broadcastAssetStates = AssetStates();
            persisted.broadcastDraftLineBindings = Array.Empty<BroadcastWorkbenchPersistedLineBindingState>();
            persisted.broadcastDraftRules = Array.Empty<BroadcastWorkbenchPersistedRuleState>();
            persisted.broadcastDraftPlatformAnnouncements = Array.Empty<BroadcastWorkbenchPersistedPlatformAnnouncementState>();
            persisted.broadcastLineBindings = Bindings();
            persisted.broadcastRules = Rules();
            persisted.broadcastPlatformAnnouncements = Platforms();
            persisted.broadcastAppliedState = Applied();
            persisted.broadcastDraftVolume = m_State.GetDraftVolume(ModeScope.DefaultWorkbench);
            persisted.broadcastVolumeStates = VolumeStates();
        }

        internal void Restore(DispatchWorkbenchPersistentState persisted)
        {
            Restore(
                persisted?.broadcastAssetDirectory,
                persisted?.broadcastAssets,
                persisted?.broadcastAssetStates,
                persisted?.broadcastDraftLineBindings,
                persisted?.broadcastDraftRules,
                persisted?.broadcastDraftPlatformAnnouncements,
                persisted?.broadcastLineBindings,
                persisted?.broadcastRules,
                persisted?.broadcastPlatformAnnouncements,
                persisted?.broadcastAppliedState,
                persisted?.broadcastDraftVolume ?? 80,
                persisted?.broadcastVolumeStates);
        }

        internal BroadcastWorkbenchPersistedAssetState[] Assets()
        {
            return Assets(ModeScope.DefaultWorkbench);
        }

        internal BroadcastWorkbenchPersistedAssetState[] Assets(ModeScope scope)
        {
            return m_State.AssetState(scope).Catalog
                .OrderBy(asset => asset?.name, StringComparer.OrdinalIgnoreCase)
                .Select(asset => asset == null
                    ? null
                    : new BroadcastWorkbenchPersistedAssetState
                    {
                        name = asset.name ?? string.Empty,
                        desc = asset.desc ?? string.Empty,
                        length = asset.length ?? string.Empty,
                        extension = asset.extension ?? string.Empty
                    })
                .Where(asset => asset != null && !string.IsNullOrEmpty(asset.name))
                .ToArray();
        }

        internal BroadcastWorkbenchPersistedAssetCatalogState[] AssetStates()
        {
            return s_PersistedAssetScopes
                .Select(scope => new BroadcastWorkbenchPersistedAssetCatalogState
                {
                    mode = scope.Token,
                    assetDirectory = m_State.AssetState(scope).AssetDir ?? string.Empty,
                    assets = Assets(scope)
                })
                .ToArray();
        }

        internal BroadcastWorkbenchPersistedLineBindingState[] DraftBindingStates()
        {
            return BindingStates(m_State.DraftBindings);
        }

        internal BroadcastWorkbenchPersistedLineBindingState[] Bindings()
        {
            return BindingStates(m_State.AppliedBindings);
        }

        internal BroadcastWorkbenchPersistedRuleState[] DraftRuleStates()
        {
            return RuleStates(m_State.DraftRules);
        }

        internal BroadcastWorkbenchPersistedRuleState[] Rules()
        {
            return RuleStates(m_State.AppliedRules);
        }

        internal BroadcastWorkbenchPersistedPlatformAnnouncementState[] DraftPlatformStates()
        {
            return PlatformStates(m_State.DraftPlatforms);
        }

        internal BroadcastWorkbenchPersistedPlatformAnnouncementState[] Platforms()
        {
            return PlatformStates(m_State.AppliedPlatforms);
        }

        internal BroadcastWorkbenchPersistedAppliedState Applied()
        {
            return new BroadcastWorkbenchPersistedAppliedState
            {
                lineIds = m_State.AppliedLines.OrderBy(lineId => lineId, StringComparer.Ordinal).ToArray(),
                volume = Preview.Clamp(m_State.GetAppliedVolume(ModeScope.DefaultWorkbench))
            };
        }

        internal BroadcastWorkbenchPersistedVolumeState[] VolumeStates()
        {
            return s_PersistedAssetScopes
                .Select(scope => new BroadcastWorkbenchPersistedVolumeState
                {
                    mode = scope.Token,
                    draftVolume = Preview.Clamp(m_State.GetDraftVolume(scope)),
                    appliedVolume = Preview.Clamp(m_State.GetAppliedVolume(scope))
                })
                .ToArray();
        }

        internal void Restore(
            string broadcastAssetDirectory,
            BroadcastWorkbenchPersistedAssetState[] persistedAssets,
            BroadcastWorkbenchPersistedAssetCatalogState[] persistedAssetStates,
            BroadcastWorkbenchPersistedLineBindingState[] persistedDraftLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedDraftRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedDraftPlatformAnnouncements,
            BroadcastWorkbenchPersistedLineBindingState[] persistedLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedPlatformAnnouncements,
            BroadcastWorkbenchPersistedAppliedState persistedAppliedState,
            int persistedDraftVolume,
            BroadcastWorkbenchPersistedVolumeState[] persistedVolumeStates)
        {
            foreach (BroadcastWorkbenchAssetState assetState in m_State.AssetsByMode.Values)
            {
                if (assetState == null)
                {
                    continue;
                }

                assetState.Catalog.Clear();
                assetState.AssetDir = string.Empty;
                assetState.BrowseDir = string.Empty;
            }
            m_State.DraftBindings.Clear();
            m_State.DraftRules.Clear();
            m_State.DraftPlatforms.Clear();
            m_State.AppliedBindings.Clear();
            m_State.AppliedRules.Clear();
            m_State.AppliedPlatforms.Clear();
            m_State.AppliedLines.Clear();
            m_State.DraftVolumesByMode.Clear();
            m_State.AppliedVolumesByMode.Clear();
            RestoreVolumes(persistedAppliedState, persistedDraftVolume, persistedVolumeStates);
            bool restoredTrainFromScopedState = RestoreAssetStates(persistedAssetStates);
            if (!restoredTrainFromScopedState)
            {
                RestoreAssetState(ModeScope.DefaultWorkbench, broadcastAssetDirectory, persistedAssets);
            }

            RapidTransitMod.Broadcasting.WorkbenchBackend.Bindings.RestoreInto(
                m_State.AppliedBindings,
                persistedLineBindings);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.RestoreInto(
                m_State.AppliedRules,
                persistedRules);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Platforms.RestoreInto(
                m_State.AppliedPlatforms,
                persistedPlatformAnnouncements);
            RemoveUnsupportedBusData();

            if (persistedAppliedState?.lineIds != null)
            {
                for (int i = 0; i < persistedAppliedState.lineIds.Length; i++)
                {
                    string lineId = persistedAppliedState.lineIds[i];
                    if (string.IsNullOrWhiteSpace(lineId))
                    {
                        continue;
                    }

                    m_State.AppliedLines.Add(lineId);
                }
            }

            m_Ctx.Preview.ApplyVolume();
            m_Announcements.ApplyVolume();
            m_Announcements.ClearLineChecks();
        }

        private void RestoreVolumes(
            BroadcastWorkbenchPersistedAppliedState persistedAppliedState,
            int persistedDraftVolume,
            BroadcastWorkbenchPersistedVolumeState[] persistedVolumeStates)
        {
            int legacyAppliedVolume = Preview.Clamp(persistedAppliedState?.volume ?? persistedDraftVolume);
            int legacyDraftVolume = Preview.Clamp(persistedDraftVolume);
            m_State.SetAppliedVolume(ModeScope.DefaultWorkbench, legacyAppliedVolume);
            m_State.SetDraftVolume(ModeScope.DefaultWorkbench, legacyDraftVolume);

            if (persistedVolumeStates == null)
            {
                return;
            }

            for (int i = 0; i < persistedVolumeStates.Length; i++)
            {
                BroadcastWorkbenchPersistedVolumeState state = persistedVolumeStates[i];
                if (state == null
                    || !ModeScope.TryParseWorkbench(state.mode, out ModeScope scope)
                    || !scope.SupportsBroadcast)
                {
                    continue;
                }

                int appliedVolume = Preview.Clamp(state.appliedVolume ?? state.draftVolume ?? 80);
                int draftVolume = Preview.Clamp(state.draftVolume ?? appliedVolume);
                m_State.SetAppliedVolume(scope, appliedVolume);
                m_State.SetDraftVolume(scope, draftVolume);
            }
        }

        private bool RestoreAssetStates(BroadcastWorkbenchPersistedAssetCatalogState[] persistedAssetStates)
        {
            bool restoredTrain = false;
            if (persistedAssetStates == null)
            {
                return false;
            }

            for (int i = 0; i < persistedAssetStates.Length; i++)
            {
                BroadcastWorkbenchPersistedAssetCatalogState state = persistedAssetStates[i];
                if (state == null
                    || !ModeScope.TryParseWorkbench(state.mode, out ModeScope scope)
                    || !scope.SupportsBroadcast)
                {
                    continue;
                }

                RestoreAssetState(scope, state.assetDirectory, state.assets);
                if (scope.Mode == ModeScope.DefaultWorkbench.Mode)
                {
                    restoredTrain = true;
                }
            }

            return restoredTrain;
        }

        private void RestoreAssetState(
            ModeScope scope,
            string broadcastAssetDirectory,
            BroadcastWorkbenchPersistedAssetState[] persistedAssets)
        {
            using (UseScope(scope))
            {
                BrowseFolder = string.Empty;
                Catalog.Clear();
                AssetFolder = m_Ctx.Assets.EnsureDir();

                string managedAssetDirectory = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Dir(AssetFolder);
                if (!string.IsNullOrEmpty(broadcastAssetDirectory))
                {
                    string persistedDirectory = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Dir(broadcastAssetDirectory);
                    string legacyRootDirectory = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.NormalizeDirectoryBrowserPath(
                        AssetScope.RootDir());
                    if (!string.IsNullOrEmpty(persistedDirectory)
                        && !string.Equals(persistedDirectory, legacyRootDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        managedAssetDirectory = persistedDirectory;
                    }
                }

                AssetFolder = managedAssetDirectory;

                if (persistedAssets == null)
                {
                    return;
                }

                for (int i = 0; i < persistedAssets.Length; i++)
                {
                    BroadcastWorkbenchPersistedAssetState asset = persistedAssets[i];
                    if (asset == null || string.IsNullOrWhiteSpace(asset.name) || string.IsNullOrEmpty(managedAssetDirectory))
                    {
                        continue;
                    }

                    string candidatePath = IoPath.Combine(managedAssetDirectory, asset.name);
                    string resolvedPath = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Path(candidatePath);

                    Catalog.Add(new BroadcastWorkbenchAssetDto
                    {
                        name = asset.name ?? string.Empty,
                        desc = !string.IsNullOrEmpty(asset.desc)
                            ? asset.desc
                            : (asset.extension ?? string.Empty).TrimStart('.').ToUpperInvariant(),
                        length = asset.length ?? string.Empty,
                        path = resolvedPath,
                        extension = !string.IsNullOrEmpty(asset.extension)
                            ? asset.extension
                            : (IoPath.GetExtension(candidatePath) ?? string.Empty),
                        missing = string.IsNullOrEmpty(resolvedPath)
                    });
                }
            }
        }

        internal BroadcastWorkbenchPersistedLineBindingState[] BindingStates(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedLineBindingState
                {
                    lineId = entry.Key ?? string.Empty,
                    stationBindings = RapidTransitMod.Broadcasting.WorkbenchBackend.Bindings.Flatten(entry.Value)
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.lineId) && entry.stationBindings.Length > 0)
                .ToArray();
        }

        internal BroadcastWorkbenchPersistedRuleState[] RuleStates(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedRuleState
                {
                    lineId = entry.Key ?? string.Empty,
                    rules = entry.Value?
                        .Select(RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.Clone)
                        .Where(rule => rule != null && (!IsBusLine(entry.Key) || IsBusRule(rule)))
                        .ToArray()
                        ?? Array.Empty<BroadcastWorkbenchRuleDto>()
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.lineId) && entry.rules.Length > 0)
                .ToArray();
        }

        internal BroadcastWorkbenchPersistedPlatformAnnouncementState[] PlatformStates(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedPlatformAnnouncementState
                {
                    lineId = entry.Key ?? string.Empty,
                    announcements = RapidTransitMod.Broadcasting.WorkbenchBackend.Platforms.Flatten(entry.Value)
                })
                .Where(entry => !IsBusLine(entry.lineId)
                    && !string.IsNullOrWhiteSpace(entry.lineId)
                    && entry.announcements.Length > 0)
                .ToArray();
        }

        private void RemoveUnsupportedBusData()
        {
            RemoveBusPlatforms(m_State.AppliedPlatforms);
            RemoveBusPlatforms(m_State.DraftPlatforms);

            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> entry in m_State.AppliedRules.ToArray())
            {
                if (!IsBusLine(entry.Key))
                {
                    continue;
                }

                List<BroadcastWorkbenchRuleDto> allowed = (entry.Value ?? new List<BroadcastWorkbenchRuleDto>())
                    .Where(IsBusRule)
                    .Select(RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.Clone)
                    .ToList();
                if (allowed.Count == 0)
                {
                    m_State.AppliedRules.Remove(entry.Key);
                }
                else
                {
                    m_State.AppliedRules[entry.Key] = allowed;
                }
            }
        }

        private static void RemoveBusPlatforms(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> platforms)
        {
            foreach (string lineId in platforms.Keys.Where(IsBusLine).ToArray())
            {
                platforms.Remove(lineId);
            }
        }

        private static bool IsBusLine(string lineId)
        {
            return LineIdentityService.TryGetMode(lineId, out TransitMode mode)
                && mode == TransitMode.Bus;
        }

        private static bool IsBusRule(BroadcastWorkbenchRuleDto rule)
        {
            string trigger = rule?.triggerId ?? rule?.trigger ?? string.Empty;
            return string.Equals(trigger, "stop_and_open", StringComparison.Ordinal)
                || string.Equals(trigger, "leave_station", StringComparison.Ordinal);
        }
    }
}
