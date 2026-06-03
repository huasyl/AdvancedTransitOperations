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
        internal Persistence(Context context) : base(context) { }

        internal void Build(DispatchWorkbenchPersistentState persisted)
        {
            if (persisted == null)
            {
                return;
            }

            persisted.broadcastAssetDirectory = m_State.AssetDir;
            persisted.broadcastAssets = Assets();
            persisted.broadcastDraftLineBindings = DraftBindingStates();
            persisted.broadcastDraftRules = DraftRuleStates();
            persisted.broadcastDraftPlatformAnnouncements = DraftPlatformStates();
            persisted.broadcastLineBindings = Bindings();
            persisted.broadcastRules = Rules();
            persisted.broadcastPlatformAnnouncements = Platforms();
            persisted.broadcastAppliedState = Applied();
            persisted.broadcastDraftVolume = m_State.DraftVolume;
        }

        internal void Restore(DispatchWorkbenchPersistentState persisted)
        {
            Restore(
                persisted?.broadcastAssetDirectory,
                persisted?.broadcastAssets,
                persisted?.broadcastDraftLineBindings,
                persisted?.broadcastDraftRules,
                persisted?.broadcastDraftPlatformAnnouncements,
                persisted?.broadcastLineBindings,
                persisted?.broadcastRules,
                persisted?.broadcastPlatformAnnouncements,
                persisted?.broadcastAppliedState,
                persisted?.broadcastDraftVolume ?? 80);
        }

        internal BroadcastWorkbenchPersistedAssetState[] Assets()
        {
            return m_State.Catalog
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
                volume = Preview.Clamp(AppliedVol)
            };
        }

        internal void Restore(
            string broadcastAssetDirectory,
            BroadcastWorkbenchPersistedAssetState[] persistedAssets,
            BroadcastWorkbenchPersistedLineBindingState[] persistedDraftLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedDraftRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedDraftPlatformAnnouncements,
            BroadcastWorkbenchPersistedLineBindingState[] persistedLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedPlatformAnnouncements,
            BroadcastWorkbenchPersistedAppliedState persistedAppliedState,
            int persistedDraftVolume)
        {
            m_State.Catalog.Clear();
            m_State.DraftBindings.Clear();
            m_State.DraftRules.Clear();
            m_State.DraftPlatforms.Clear();
            m_State.AppliedBindings.Clear();
            m_State.AppliedRules.Clear();
            m_State.AppliedPlatforms.Clear();
            m_State.AppliedLines.Clear();
            m_Announcements.ClearLineChecks();
            DraftVol = Preview.Clamp(persistedDraftVolume);
            AppliedVol = Preview.Clamp(persistedAppliedState?.volume ?? DraftVol);
            BrowseFolder = string.Empty;
            AssetFolder = m_Ctx.Assets.EnsureDir();

            string managedAssetDirectory = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Dir(AssetFolder);
            if (!string.IsNullOrEmpty(broadcastAssetDirectory))
            {
                string persistedDirectory = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Dir(broadcastAssetDirectory);
                if (!string.IsNullOrEmpty(persistedDirectory))
                {
                    managedAssetDirectory = persistedDirectory;
                }
            }

            AssetFolder = managedAssetDirectory;

            if (persistedAssets != null)
            {
                for (int i = 0; i < persistedAssets.Length; i++)
                {
                    BroadcastWorkbenchPersistedAssetState asset = persistedAssets[i];
                    if (asset == null || string.IsNullOrWhiteSpace(asset.name) || string.IsNullOrEmpty(managedAssetDirectory))
                    {
                        continue;
                    }

                    string candidatePath = IoPath.Combine(managedAssetDirectory, asset.name);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    m_State.Catalog.Add(new BroadcastWorkbenchAssetDto
                    {
                        name = asset.name ?? string.Empty,
                        desc = !string.IsNullOrEmpty(asset.desc)
                            ? asset.desc
                            : (asset.extension ?? string.Empty).TrimStart('.').ToUpperInvariant(),
                        length = asset.length ?? string.Empty,
                        path = RapidTransitMod.Broadcasting.WorkbenchBackend.Assets.Path(candidatePath),
                        extension = !string.IsNullOrEmpty(asset.extension)
                            ? asset.extension
                            : (IoPath.GetExtension(candidatePath) ?? string.Empty)
                    });
                }
            }

            RapidTransitMod.Broadcasting.WorkbenchBackend.Bindings.RestoreInto(
                m_State.DraftBindings,
                persistedDraftLineBindings);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.RestoreInto(
                m_State.DraftRules,
                persistedDraftRules);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Platforms.RestoreInto(
                m_State.DraftPlatforms,
                persistedDraftPlatformAnnouncements);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Bindings.RestoreInto(
                m_State.AppliedBindings,
                persistedLineBindings);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.RestoreInto(
                m_State.AppliedRules,
                persistedRules);

            RapidTransitMod.Broadcasting.WorkbenchBackend.Platforms.RestoreInto(
                m_State.AppliedPlatforms,
                persistedPlatformAnnouncements);

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

            if (m_State.DraftBindings.Count == 0 && persistedLineBindings != null)
            {
                RapidTransitMod.Broadcasting.WorkbenchBackend.Bindings.Copy(m_State.AppliedBindings, m_State.DraftBindings);
            }

            if (m_State.DraftRules.Count == 0 && persistedRules != null)
            {
                RapidTransitMod.Broadcasting.WorkbenchBackend.Rules.Copy(m_State.AppliedRules, m_State.DraftRules);
            }

            if (m_State.DraftPlatforms.Count == 0 && persistedPlatformAnnouncements != null)
            {
                RapidTransitMod.Broadcasting.WorkbenchBackend.Platforms.Copy(m_State.AppliedPlatforms, m_State.DraftPlatforms);
            }

            m_Ctx.Preview.ApplyVolume();
            m_Announcements.ApplyVolume();
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
                        .Where(rule => rule != null)
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
                .Where(entry => !string.IsNullOrWhiteSpace(entry.lineId) && entry.announcements.Length > 0)
                .ToArray();
        }
    }
}
