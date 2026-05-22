using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
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

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private const string BroadcastAssetBrowserDrivesToken = "__drives__";
        private const string BroadcastManagedAssetDirectoryName = "BroadcastAssets";
        private const float BroadcastVolumeScalarMin = 5f;
        private const float BroadcastVolumeScalarMax = 20f;
        private static readonly string[] s_BroadcastAssetExtensions = { ".wav", ".mp3", ".ogg" };
        private readonly List<BroadcastWorkbenchAssetDto> m_BroadcastAssetCatalog = new List<BroadcastWorkbenchAssetDto>();
        private string m_BroadcastAssetDirectory = string.Empty;
        private string m_BroadcastExternalBrowseDirectory = string.Empty;
        private readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> m_BroadcastDraftLineStationAssetBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> m_BroadcastDraftLegacyLineStationAssetBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BroadcastWorkbenchRuleDto>> m_BroadcastDraftLineRules =
            new Dictionary<string, List<BroadcastWorkbenchRuleDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> m_BroadcastDraftLinePlatformAnnouncements =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> m_BroadcastDraftLegacyLinePlatformAnnouncements =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> m_BroadcastLineStationAssetBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> m_BroadcastLegacyLineStationAssetBindings =
            new Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BroadcastWorkbenchRuleDto>> m_BroadcastLineRules =
            new Dictionary<string, List<BroadcastWorkbenchRuleDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> m_BroadcastLinePlatformAnnouncements =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> m_BroadcastLegacyLinePlatformAnnouncements =
            new Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, DispatchWorkbenchStationConflictDto[]>> m_BroadcastPendingAutoBindConflicts =
            new Dictionary<string, Dictionary<string, DispatchWorkbenchStationConflictDto[]>>(StringComparer.Ordinal);
        private readonly HashSet<string> m_BroadcastAppliedLineIds = new HashSet<string>(StringComparer.Ordinal);
        private int m_BroadcastDraftVolumePercent = 80;
        private int m_BroadcastAppliedVolumePercent = 80;
        private AudioSource m_BroadcastPreviewAudioSource;
        private AudioClip m_BroadcastPreviewAudioClip;
        private string m_BroadcastPreviewAssetName = string.Empty;
        private int m_BroadcastPreviewPlaybackToken;
        private AudioSource m_BroadcastRulePreviewAudioSource;
        private AudioClip m_BroadcastRulePreviewAudioClip;
        private string m_BroadcastPreviewRuleId = string.Empty;
        private int m_BroadcastRulePreviewPlaybackToken;
        private static readonly FieldInfo s_AudioManagerUiGroupField =
            typeof(AudioManager).GetField("m_UIGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        [DataContract]
        public class BroadcastWorkbenchAssetDto
        {
            [DataMember]
            public string name;
            [DataMember]
            public string desc;
            [DataMember]
            public string length;
            [DataMember]
            public string path;
            [DataMember]
            public string extension;
        }

        [DataContract]
        public class BroadcastWorkbenchSnapshot
        {
            [DataMember]
            public string selectedLineId;
            [DataMember]
            public DispatchWorkbenchLineDto[] lines;
            [DataMember]
            public DispatchWorkbenchStationDto[] stations;
            [DataMember]
            public BroadcastWorkbenchTurnbackPointDto[] turnbackPoints;
            [DataMember]
            public BroadcastWorkbenchStationBindingDto[] stationBindings;
            [DataMember]
            public BroadcastWorkbenchRuleDto[] rules;
            [DataMember]
            public BroadcastWorkbenchPlatformAnnouncementDto[] platformAnnouncements;
            [DataMember]
            public string assetDirectory;
            [DataMember]
            public BroadcastWorkbenchAssetDto[] assets;
            [DataMember]
            public string version;
            [DataMember]
            public string sourceMode;
            [DataMember]
            public bool lineApplied;
            [DataMember]
            public bool lineDraftDirty;
            [DataMember]
            public bool volumeDirty;
            [DataMember]
            public bool draftApplied;
            [DataMember]
            public bool draftDirty;
            [DataMember]
            public int volume;
            [DataMember]
            public string[] warnings;
        }

        [DataContract]
        public class BroadcastWorkbenchTurnbackPointDto
        {
            [DataMember]
            public int index;
            [DataMember]
            public string stationId;
            [DataMember]
            public string stationName;
            [DataMember]
            public bool resolved;
        }

        [DataContract]
        public class BroadcastWorkbenchDirectoryPickerResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public bool pending;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchExternalAssetFileDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string name;
            [DataMember]
            public string fullPath;
        }

        [DataContract]
        public class BroadcastWorkbenchStationBindingDto
        {
            [DataMember]
            public string stationId;
            [DataMember]
            public string lang;
            [DataMember]
            public int langIndex;
            [DataMember]
            public string assetName;
        }

        [DataContract]
        public class BroadcastWorkbenchRuleNodeDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string type;
            [DataMember]
            public string name;
            [DataMember]
            public string nameKey;
            [DataMember]
            public string desc;
            [DataMember]
            public string descKey;
            [DataMember]
            public int langIndex;
            [DataMember]
            public float delaySeconds;
        }

        [DataContract]
        public class BroadcastWorkbenchRuleDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string title;
            [DataMember]
            public string titleKey;
            [DataMember]
            public string triggerId;
            [DataMember]
            public string trigger;
            [DataMember]
            public string triggerKey;
            [DataMember]
            public BroadcastWorkbenchRuleNodeDto[] nodes;
        }

        [DataContract]
        public class BroadcastWorkbenchPlatformAnnouncementDto
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public string stationId;
            [DataMember]
            public string stationName;
            [DataMember]
            public string title;
            [DataMember]
            public string uiTriggerId;
            [DataMember]
            public bool enabled;
            [DataMember]
            public string triggerId;
            [DataMember]
            public int cooldownGameMinutes;
            [DataMember]
            public BroadcastWorkbenchRuleNodeDto[] nodes;
        }

        [DataContract]
        public class BroadcastWorkbenchExternalAssetBrowserSnapshot
        {
            [DataMember]
            public string rootPath;
            [DataMember]
            public string currentPath;
            [DataMember]
            public string parentPath;
            [DataMember]
            public string[] folders;
            [DataMember]
            public BroadcastWorkbenchExternalAssetFileDto[] files;
            [DataMember]
            public string[] allowedExtensions;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchImportExternalAssetsRequest
        {
            [DataMember]
            public string currentPath;
            [DataMember]
            public string[] selectedPaths;
        }

        [DataContract]
        public class BroadcastWorkbenchImportExternalAssetsResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public int importedCount;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchAssetPreviewResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string state;
            [DataMember]
            public string error;
            [DataMember]
            public string assetName;
        }

        [DataContract]
        public class BroadcastWorkbenchAssetPreviewStateDto
        {
            [DataMember]
            public string assetName;
            [DataMember]
            public string state;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchRulePreviewRequest
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public string ruleId;
            [DataMember]
            public int volume;
        }

        [DataContract]
        public class BroadcastWorkbenchRulePreviewResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string state;
            [DataMember]
            public string error;
            [DataMember]
            public string ruleId;
        }

        [DataContract]
        public class BroadcastWorkbenchRulePreviewStateDto
        {
            [DataMember]
            public string ruleId;
            [DataMember]
            public string state;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchVolumeResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
            [DataMember]
            public int volume;
            [DataMember]
            public bool volumeDirty;
            [DataMember]
            public BroadcastWorkbenchSnapshot snapshot;
        }

        [DataContract]
        public class BroadcastWorkbenchBindingSlotHintDto
        {
            [DataMember]
            public int langIndex;
            [DataMember]
            public string[] labels;
        }

        [DataContract]
        public class BroadcastWorkbenchBindingSlotHintsResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
            [DataMember]
            public BroadcastWorkbenchBindingSlotHintDto[] slotHints;
        }

        [DataContract]
        public class BroadcastWorkbenchDeleteAssetResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchDeleteAllAssetsResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchAutoBindStationMappingsResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public int boundCount;
            [DataMember]
            public string error;
        }

        private sealed class BroadcastWorkbenchStationGroup
        {
            public string Key = string.Empty;
            public DispatchWorkbenchStationDto Representative;
            public List<string> StationIds = new List<string>();
            public Entity AnchorEntity;
        }

        [DataContract]
        public class BroadcastWorkbenchSaveStationBindingRequest
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public string stationId;
            [DataMember]
            public string assetName;
        }

        [DataContract]
        public class BroadcastWorkbenchSaveStationBindingsRequest
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public string stationId;
            [DataMember]
            public BroadcastWorkbenchStationBindingDto[] bindings;
        }

        [DataContract]
        public class BroadcastWorkbenchSaveStationBindingResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchSaveRulesRequest
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public BroadcastWorkbenchRuleDto[] rules;
        }

        [DataContract]
        public class BroadcastWorkbenchSaveRulesResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
        }

        [DataContract]
        public class BroadcastWorkbenchSavePlatformAnnouncementRequest
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public string stationId;
            [DataMember]
            public string stationName;
            [DataMember]
            public string title;
            [DataMember]
            public string uiTriggerId;
            [DataMember]
            public bool enabled;
            [DataMember]
            public BroadcastWorkbenchRuleNodeDto[] nodes;
        }

        [DataContract]
        public class BroadcastWorkbenchSavePlatformAnnouncementResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
            [DataMember]
            public BroadcastWorkbenchSnapshot snapshot;
        }

        [DataContract]
        public class BroadcastWorkbenchApplyRequest
        {
            [DataMember]
            public string lineId;
        }

        [DataContract]
        public class BroadcastWorkbenchApplyResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string error;
            [DataMember]
            public BroadcastWorkbenchSnapshot snapshot;
        }

        [DataContract]
        public class BroadcastWorkbenchPersistedAssetState
        {
            [DataMember]
            public string name;
            [DataMember]
            public string desc;
            [DataMember]
            public string length;
            [DataMember]
            public string extension;
        }

        [DataContract]
        public class BroadcastWorkbenchPersistedLineBindingState
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public BroadcastWorkbenchStationBindingDto[] stationBindings;
        }

        [DataContract]
        public class BroadcastWorkbenchPersistedRuleState
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public BroadcastWorkbenchRuleDto[] rules;
        }

        [DataContract]
        public class BroadcastWorkbenchPersistedPlatformAnnouncementState
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public BroadcastWorkbenchPlatformAnnouncementDto[] announcements;
        }

        [DataContract]
        public class BroadcastWorkbenchPersistedAppliedState
        {
            [DataMember]
            public string[] lineIds;
            [DataMember]
            public int? volume;
        }

        public string LoadBroadcastWorkbenchSnapshotJson(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildBroadcastWorkbenchSnapshot(preferredLineId));
        }

        public string RefreshBroadcastWorkbenchSnapshotJson(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildBroadcastWorkbenchSnapshot(preferredLineId));
        }

        public string LoadBroadcastBindingSlotHintsJson(string lineId)
        {
            BroadcastWorkbenchBindingSlotHintsResult result = new BroadcastWorkbenchBindingSlotHintsResult
            {
                success = false,
                error = string.Empty,
                slotHints = Array.Empty<BroadcastWorkbenchBindingSlotHintDto>()
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                string resolvedLineId = lineId?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(resolvedLineId))
                {
                    result.error = "Line is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                result.success = true;
                result.slotHints = BuildBroadcastBindingSlotHints(resolvedLineId);
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("LoadBroadcastBindingSlotHintsJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string LoadBroadcastAssetBrowserJson(string requestedPath)
        {
            return DispatchWorkbenchJson.Serialize(BuildBroadcastAssetBrowserSnapshot(requestedPath));
        }

        public string SaveBroadcastRulesJson(string requestJson)
        {
            BroadcastWorkbenchSaveRulesResult result = new BroadcastWorkbenchSaveRulesResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchSaveRulesRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchSaveRulesRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(lineId))
                {
                    result.error = "Line is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                List<BroadcastWorkbenchRuleDto> normalizedRules = NormalizeBroadcastRules(request?.rules);
                if (normalizedRules.Count == 0)
                {
                    m_BroadcastDraftLineRules.Remove(lineId);
                }
                else
                {
                    m_BroadcastDraftLineRules[lineId] = normalizedRules;
                }

                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(lineId));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("SaveBroadcastRulesJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string SaveBroadcastPlatformAnnouncementJson(string requestJson)
        {
            return SaveBroadcastPlatformAnnouncementCore(requestJson, copyToAllStations: false);
        }

        public string CopyBroadcastPlatformAnnouncementToAllStationsJson(string requestJson)
        {
            return SaveBroadcastPlatformAnnouncementCore(requestJson, copyToAllStations: true);
        }

        private string SaveBroadcastPlatformAnnouncementCore(string requestJson, bool copyToAllStations)
        {
            BroadcastWorkbenchSavePlatformAnnouncementResult result = new BroadcastWorkbenchSavePlatformAnnouncementResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchSavePlatformAnnouncementRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchSavePlatformAnnouncementRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                string stationId = request?.stationId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(stationId))
                {
                    result.error = "Line or station is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
                WorkbenchLineRuntime activeRuntime = ResolveActiveWorkbenchLine(runtimeLines, lineId);
                List<BroadcastWorkbenchStationGroup> stationGroups = new List<BroadcastWorkbenchStationGroup>();
                if (activeRuntime != null)
                {
                    EnsureBroadcastLineStateMigrated(lineId, activeRuntime.Entity, out stationGroups);
                }
                BroadcastWorkbenchPlatformAnnouncementDto announcement =
                    NormalizeBroadcastPlatformAnnouncement(lineId, stationId, request?.stationName, request?.title, request?.uiTriggerId, request?.enabled == true, request?.nodes);
                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements =
                    GetOrCreateBroadcastDraftLinePlatformAnnouncements(lineId);

                if (copyToAllStations)
                {
                    foreach (BroadcastWorkbenchStationGroup group in stationGroups)
                    {
                        string targetStationId = group?.Representative?.id ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(targetStationId))
                        {
                            continue;
                        }

                        BroadcastWorkbenchPlatformAnnouncementDto clonedAnnouncement = CloneBroadcastPlatformAnnouncement(
                            announcement,
                            lineId,
                            targetStationId,
                            group.Representative.name);
                        lineAnnouncements[BuildBroadcastPlatformAnnouncementStorageKey(
                            targetStationId,
                            clonedAnnouncement.uiTriggerId)] = clonedAnnouncement;
                    }
                }
                else
                {
                    lineAnnouncements[BuildBroadcastPlatformAnnouncementStorageKey(
                        stationId,
                        announcement.uiTriggerId)] = announcement;
                }

                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;
                result.snapshot = BuildBroadcastWorkbenchSnapshot(lineId);
                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(result.snapshot);
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("SaveBroadcastPlatformAnnouncementJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string ImportBroadcastExternalAssetsJson(string requestJson)
        {
            BroadcastWorkbenchImportExternalAssetsResult result = new BroadcastWorkbenchImportExternalAssetsResult
            {
                success = false,
                importedCount = 0,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchImportExternalAssetsRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchImportExternalAssetsRequest>(requestJson);
                string[] selectedPaths = request?.selectedPaths ?? Array.Empty<string>();
                if (selectedPaths.Length == 0)
                {
                    result.error = "No files selected.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                string managedAssetDirectory = EnsureBroadcastManagedAssetDirectory();
                if (string.IsNullOrEmpty(managedAssetDirectory))
                {
                    result.error = "Broadcast asset directory is unavailable.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                HashSet<string> allowedExtensions = new HashSet<string>(s_BroadcastAssetExtensions, StringComparer.OrdinalIgnoreCase);
                HashSet<string> existingNames = new HashSet<string>(
                    m_BroadcastAssetCatalog
                        .Select(asset => asset?.name)
                        .Where(name => !string.IsNullOrWhiteSpace(name)),
                    StringComparer.OrdinalIgnoreCase);

                List<BroadcastWorkbenchAssetDto> importedAssets = new List<BroadcastWorkbenchAssetDto>();
                for (int i = 0; i < selectedPaths.Length; i++)
                {
                    string normalizedFilePath = NormalizeBroadcastAssetFilePath(selectedPaths[i]);
                    if (string.IsNullOrEmpty(normalizedFilePath))
                    {
                        continue;
                    }

                    string extension = Path.GetExtension(normalizedFilePath);
                    if (string.IsNullOrEmpty(extension) || !allowedExtensions.Contains(extension))
                    {
                        continue;
                    }

                    string destinationFileName = Path.GetFileName(normalizedFilePath);
                    if (string.IsNullOrEmpty(destinationFileName) || !existingNames.Add(destinationFileName))
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(managedAssetDirectory, destinationFileName);
                    if (!File.Exists(destinationPath))
                    {
                        File.Copy(normalizedFilePath, destinationPath, false);
                    }

                    importedAssets.Add(CreateBroadcastWorkbenchAssetDto(destinationPath));
                }

                if (importedAssets.Count == 0)
                {
                    result.success = true;
                    return DispatchWorkbenchJson.Serialize(result);
                }

                m_BroadcastAssetCatalog.AddRange(importedAssets);
                m_BroadcastAssetCatalog.Sort((left, right) =>
                    string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));

                string normalizedCurrentPath = NormalizeBroadcastAssetDirectory(request?.currentPath);
                if (!string.IsNullOrEmpty(normalizedCurrentPath))
                {
                    m_BroadcastExternalBrowseDirectory = normalizedCurrentPath;
                }

                m_BroadcastAssetDirectory = managedAssetDirectory;
                m_BroadcastPendingAutoBindConflicts.Clear();
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;
                result.importedCount = importedAssets.Count;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("ImportBroadcastExternalAssetsJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string SaveBroadcastStationBindingJson(string requestJson)
        {
            BroadcastWorkbenchSaveStationBindingResult result = new BroadcastWorkbenchSaveStationBindingResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchSaveStationBindingRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchSaveStationBindingRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                string stationId = request?.stationId ?? string.Empty;
                string assetName = request?.assetName ?? string.Empty;
                if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(stationId))
                {
                    result.error = "Line or station is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                if (!string.IsNullOrEmpty(assetName)
                    && !m_BroadcastAssetCatalog.Any(asset => string.Equals(asset?.name, assetName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.error = "Selected asset was not found.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                BroadcastWorkbenchStationBindingDto[] nextBindings = string.IsNullOrEmpty(assetName)
                    ? Array.Empty<BroadcastWorkbenchStationBindingDto>()
                    : new[]
                    {
                        new BroadcastWorkbenchStationBindingDto
                        {
                            stationId = stationId,
                            langIndex = 1,
                            assetName = assetName
                        }
                    };

                SaveBroadcastStationBindings(lineId, stationId, nextBindings);
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(lineId));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("SaveBroadcastStationBindingJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string SaveBroadcastStationBindingsJson(string requestJson)
        {
            BroadcastWorkbenchSaveStationBindingResult result = new BroadcastWorkbenchSaveStationBindingResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchSaveStationBindingsRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchSaveStationBindingsRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                string stationId = request?.stationId ?? string.Empty;
                if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(stationId))
                {
                    result.error = "Line or station is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                ValidateBroadcastStationBindings(request?.bindings);
                SaveBroadcastStationBindings(lineId, stationId, request?.bindings);
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(lineId));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("SaveBroadcastStationBindingsJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        private void SaveBroadcastStationBindings(
            string lineId,
            string stationId,
            IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
        {
            List<BroadcastWorkbenchStationBindingDto> normalizedBindings =
                NormalizeBroadcastStationBindings(stationId, bindings);
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetOrCreateBroadcastDraftLineStationBindings(lineId);
            ClearBroadcastPendingAutoBindConflict(lineId, stationId);
            if (normalizedBindings.Count == 0)
            {
                lineBindings.Remove(stationId);
            }
            else
            {
                lineBindings[stationId] = CloneBroadcastStationBindings(stationId, normalizedBindings);
            }

            if (lineBindings.Count == 0)
            {
                m_BroadcastDraftLineStationAssetBindings.Remove(lineId);
            }
        }

        private void ValidateBroadcastStationBindings(IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
        {
            if (bindings == null)
            {
                return;
            }

            foreach (BroadcastWorkbenchStationBindingDto binding in bindings)
            {
                string assetName = binding?.assetName ?? string.Empty;
                if (string.IsNullOrEmpty(assetName))
                {
                    continue;
                }

                if (!m_BroadcastAssetCatalog.Any(asset => string.Equals(asset?.name, assetName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Selected asset was not found.");
                }
            }
        }

        public string DeleteBroadcastAssetJson(string assetName)
        {
            BroadcastWorkbenchDeleteAssetResult result = new BroadcastWorkbenchDeleteAssetResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                string normalizedAssetName = assetName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(normalizedAssetName))
                {
                    result.error = "Asset name is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                if (!RemoveBroadcastAsset(normalizedAssetName))
                {
                    result.error = "Selected asset was not found.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                m_BroadcastPendingAutoBindConflicts.Clear();
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("DeleteBroadcastAssetJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string DeleteAllBroadcastAssetsJson()
        {
            BroadcastWorkbenchDeleteAllAssetsResult result = new BroadcastWorkbenchDeleteAllAssetsResult
            {
                success = false,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                RemoveAllBroadcastAssets();
                m_BroadcastPendingAutoBindConflicts.Clear();
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                result.success = true;

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("DeleteAllBroadcastAssetsJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string AutoBindBroadcastStationMappingsJson(string lineId)
        {
            BroadcastWorkbenchAutoBindStationMappingsResult result = new BroadcastWorkbenchAutoBindStationMappingsResult
            {
                success = false,
                boundCount = 0,
                error = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                EnsureAppliedWorkbenchPersistenceLoaded();

                string resolvedLineId = lineId?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(resolvedLineId))
                {
                    result.error = "Line is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
                WorkbenchLineRuntime activeRuntime = runtimeLines.FirstOrDefault(runtime =>
                    runtime != null
                    && string.Equals(runtime.Id, resolvedLineId, StringComparison.Ordinal));
                if (activeRuntime == null)
                {
                    result.error = "Selected line was not found.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                EnsureBroadcastLineStateMigrated(resolvedLineId, activeRuntime.Entity, out List<BroadcastWorkbenchStationGroup> stationGroups);
                if (stationGroups.Count == 0 || m_BroadcastAssetCatalog.Count == 0)
                {
                    ClearBroadcastPendingAutoBindConflicts(resolvedLineId);
                    result.success = true;
                    return DispatchWorkbenchJson.Serialize(result);
                }

                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings = GetOrCreateBroadcastDraftLineStationBindings(resolvedLineId);
                ClearBroadcastPendingAutoBindConflicts(resolvedLineId);
                for (int i = 0; i < stationGroups.Count; i++)
                {
                    BroadcastWorkbenchStationGroup group = stationGroups[i];
                    string stationName = group?.Representative?.name ?? string.Empty;
                    string targetStationId = group?.Representative?.id ?? string.Empty;
                    if (group == null || string.IsNullOrWhiteSpace(targetStationId))
                    {
                        continue;
                    }

                    string stationKey = NormalizeBroadcastStationMatchKey(stationName);
                    List<DispatchWorkbenchStationConflictDto> matchedAssets =
                        BuildBroadcastStationConflictCandidates(stationKey, null);
                    if (string.IsNullOrEmpty(stationKey)
                        || matchedAssets == null
                        || matchedAssets.Count != 1)
                    {
                        continue;
                    }

                    string matchedAssetName = matchedAssets[0]?.assetName ?? string.Empty;
                    if (string.IsNullOrEmpty(matchedAssetName))
                    {
                        continue;
                    }

                    bool changed = false;
                    if (!lineBindings.TryGetValue(targetStationId, out List<BroadcastWorkbenchStationBindingDto> currentBindings)
                        || currentBindings == null
                        || !currentBindings.Any(binding => binding != null && !string.IsNullOrWhiteSpace(binding.assetName)))
                    {
                        lineBindings[targetStationId] = new List<BroadcastWorkbenchStationBindingDto>
                        {
                            new BroadcastWorkbenchStationBindingDto
                            {
                                stationId = targetStationId,
                                langIndex = 1,
                                assetName = matchedAssetName
                            }
                        };
                        changed = true;
                    }

                    if (!changed)
                    {
                        continue;
                    }

                    result.boundCount++;
                }

                Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup = BuildBroadcastAssetConflictLookup();
                ApplyBroadcastStationConflicts(stationGroups, conflictLookup, resolvedLineId);
                StoreBroadcastPendingAutoBindConflicts(resolvedLineId, stationGroups);

                result.success = true;
                if (result.boundCount <= 0)
                {
                    if (lineBindings.Count == 0)
                    {
                        m_BroadcastDraftLineStationAssetBindings.Remove(resolvedLineId);
                    }

                    return DispatchWorkbenchJson.Serialize(result);
                }

                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();

                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                    BuildBroadcastWorkbenchSnapshot(resolvedLineId));
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("AutoBindBroadcastStationMappingsJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

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
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchApplyRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchApplyRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(lineId))
                {
                    result.error = "Line is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                ApplyBroadcastDraftToRuntime(lineId);
                m_BroadcastAppliedLineIds.Add(lineId);
                m_BroadcastAppliedVolumePercent = ClampBroadcastVolumePercent(m_BroadcastDraftVolumePercent);
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
                ApplyBroadcastAppliedVolumeToRuntime();

                BroadcastWorkbenchSnapshot snapshot = BuildBroadcastWorkbenchSnapshot(lineId);
                result.success = true;
                result.snapshot = snapshot;
                DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(snapshot);
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("ApplyBroadcastConfigJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string OpenBroadcastAssetDirectoryPickerJson()
        {
            BroadcastWorkbenchDirectoryPickerResult result = new BroadcastWorkbenchDirectoryPickerResult
            {
                success = false,
                pending = false,
                error = string.Empty
            };

            try
            {
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try
                    {
                        GameScreenUISystem gameScreenSystem =
                            World.DefaultGameObjectInjectionWorld?.GetOrCreateSystemManaged<GameScreenUISystem>();
                        OptionsUISystem optionsSystem =
                            World.DefaultGameObjectInjectionWorld?.GetOrCreateSystemManaged<OptionsUISystem>();
                        if (optionsSystem == null || gameScreenSystem == null)
                        {
                            Mod.log.Info("[BroadcastWorkbench] Options UI unavailable for directory picker.");
                            return;
                        }

                        GameScreenUISystem.GameScreen previousScreen = gameScreenSystem.activeScreen;
                        optionsSystem.OpenPage("Modding", null, false);
                        optionsSystem.OpenDirectoryBrowser(ResolveBroadcastAssetDirectoryRoot(), directory =>
                        {
                            try
                            {
                                ApplyBroadcastAssetDirectorySelection(directory);
                                gameScreenSystem.activeScreen = previousScreen;
                            }
                            catch (Exception ex)
                            {
                                LogBroadcastWorkbenchException("ApplyBroadcastAssetDirectorySelection", ex);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        LogBroadcastWorkbenchException("OpenBroadcastAssetDirectoryPicker", ex);
                    }
                });

                result.success = true;
                result.pending = true;
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("OpenBroadcastAssetDirectoryPickerJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string PlayBroadcastAssetPreviewJson(string assetName)
        {
            BroadcastWorkbenchAssetPreviewResult result = new BroadcastWorkbenchAssetPreviewResult
            {
                success = false,
                state = "error",
                error = string.Empty,
                assetName = assetName ?? string.Empty
            };

            try
            {
                string requestedAssetName = assetName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(requestedAssetName))
                {
                    result.error = "Asset name is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                MainThreadDispatcher.RunOnMainThread(() =>
                    StopBroadcastRulePreviewOnMainThread(m_BroadcastPreviewRuleId, notify: true));

                MainThreadDispatcher.RunOnMainThread(async () =>
                {
                    try
                    {
                        await PlayBroadcastAssetPreviewOnMainThreadAsync(requestedAssetName);
                    }
                    catch (Exception ex)
                    {
                        NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "error", ex.Message ?? string.Empty);
                        LogBroadcastWorkbenchException("PlayBroadcastAssetPreviewOnMainThreadAsync", ex);
                    }
                });

                result.success = true;
                result.state = "pending";
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("PlayBroadcastAssetPreviewJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string PlayBroadcastRulePreviewJson(string requestJson)
        {
            BroadcastWorkbenchRulePreviewResult result = new BroadcastWorkbenchRulePreviewResult
            {
                success = false,
                state = "error",
                error = string.Empty,
                ruleId = string.Empty
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                BroadcastWorkbenchRulePreviewRequest request =
                    DispatchWorkbenchJson.Deserialize<BroadcastWorkbenchRulePreviewRequest>(requestJson);
                string lineId = request?.lineId ?? string.Empty;
                string ruleId = request?.ruleId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(ruleId))
                {
                    result.error = "Line or rule is missing.";
                    return DispatchWorkbenchJson.Serialize(result);
                }

                result.success = true;
                result.state = "pending";
                result.ruleId = ruleId;
                MainThreadDispatcher.RunOnMainThread(async () =>
                {
                    try
                    {
                        await PlayBroadcastRulePreviewOnMainThreadAsync(lineId, ruleId);
                    }
                    catch (Exception ex)
                    {
                        NotifyBroadcastRulePreviewStateChanged(ruleId, "error", ex.Message ?? string.Empty);
                        LogBroadcastWorkbenchException("PlayBroadcastRulePreviewOnMainThreadAsync", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("PlayBroadcastRulePreviewJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string StopBroadcastAssetPreviewJson(string assetName)
        {
            BroadcastWorkbenchAssetPreviewResult result = new BroadcastWorkbenchAssetPreviewResult
            {
                success = true,
                state = "stopped",
                error = string.Empty,
                assetName = assetName ?? string.Empty
            };

            try
            {
                string requestedAssetName = assetName ?? string.Empty;
                MainThreadDispatcher.RunOnMainThread(() => StopBroadcastAssetPreviewOnMainThread(requestedAssetName, notify: true));
            }
            catch (Exception ex)
            {
                result.success = false;
                result.state = "error";
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("StopBroadcastAssetPreviewJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string StopBroadcastRulePreviewJson(string ruleId)
        {
            BroadcastWorkbenchRulePreviewResult result = new BroadcastWorkbenchRulePreviewResult
            {
                success = true,
                state = "stopped",
                error = string.Empty,
                ruleId = ruleId ?? string.Empty
            };

            try
            {
                string requestedRuleId = ruleId ?? string.Empty;
                MainThreadDispatcher.RunOnMainThread(() => StopBroadcastRulePreviewOnMainThread(requestedRuleId, notify: true));
            }
            catch (Exception ex)
            {
                result.success = false;
                result.state = "error";
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("StopBroadcastRulePreviewJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        public string SetBroadcastPreviewVolumeJson(string volumeJson)
        {
            BroadcastWorkbenchVolumeResult result = new BroadcastWorkbenchVolumeResult
            {
                success = false,
                error = string.Empty,
                volume = ClampBroadcastVolumePercent(m_BroadcastDraftVolumePercent),
                volumeDirty = m_BroadcastDraftVolumePercent != m_BroadcastAppliedVolumePercent,
                snapshot = null
            };

            try
            {
                EnsureWorkbenchPersistenceLoaded();
                int nextVolume = ParseBroadcastVolumePercent(volumeJson, m_BroadcastDraftVolumePercent);
                bool changed = nextVolume != m_BroadcastDraftVolumePercent;
                m_BroadcastDraftVolumePercent = nextVolume;
                ApplyBroadcastPreviewVolumeToActivePreviewSources();
                if (changed)
                {
                    m_WorkbenchSnapshotVersion++;
                    SaveWorkbenchPersistence();
                }

                result.success = true;
                result.volume = ClampBroadcastVolumePercent(m_BroadcastDraftVolumePercent);
                result.volumeDirty = m_BroadcastDraftVolumePercent != m_BroadcastAppliedVolumePercent;
            }
            catch (Exception ex)
            {
                result.error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("SetBroadcastPreviewVolumeJson", ex);
            }

            return DispatchWorkbenchJson.Serialize(result);
        }

        private BroadcastWorkbenchSnapshot BuildBroadcastWorkbenchSnapshot(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();

            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            WorkbenchLineRuntime activeRuntime = runtimeLines.Count > 0
                ? ResolveActiveWorkbenchLine(runtimeLines, preferredLineId)
                : null;
            List<BroadcastWorkbenchStationGroup> stationGroups = new List<BroadcastWorkbenchStationGroup>();
            if (activeRuntime != null
                && EnsureBroadcastLineStateMigrated(activeRuntime.Id, activeRuntime.Entity, out List<BroadcastWorkbenchStationGroup> migratedGroups))
            {
                stationGroups = migratedGroups;
                m_WorkbenchSnapshotVersion++;
                SaveWorkbenchPersistence();
            }
            else if (activeRuntime != null)
            {
                stationGroups = BuildBroadcastWorkbenchStationGroups(activeRuntime.Entity);
            }
            ApplyBroadcastPendingAutoBindConflicts(stationGroups, activeRuntime?.Id ?? string.Empty);
            BroadcastWorkbenchStationBindingDto[] stationBindings = activeRuntime != null
                ? BuildBroadcastDraftStationBindings(activeRuntime.Id, stationGroups)
                : Array.Empty<BroadcastWorkbenchStationBindingDto>();
            BroadcastWorkbenchRuleDto[] rules = activeRuntime != null
                ? BuildBroadcastDraftRules(activeRuntime.Id)
                : Array.Empty<BroadcastWorkbenchRuleDto>();
            BroadcastWorkbenchPlatformAnnouncementDto[] platformAnnouncements = activeRuntime != null
                ? BuildBroadcastDraftPlatformAnnouncements(activeRuntime.Id, stationGroups)
                : Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
            string activeLineId = activeRuntime?.Id ?? string.Empty;
            bool lineDraftDirty = !string.IsNullOrEmpty(activeLineId) && IsBroadcastLineDraftDirty(activeLineId);
            bool lineApplied = !string.IsNullOrEmpty(activeLineId)
                && m_BroadcastAppliedLineIds.Contains(activeLineId);
            bool volumeDirty = m_BroadcastDraftVolumePercent != m_BroadcastAppliedVolumePercent;
            bool draftDirty = lineDraftDirty;
            if (volumeDirty)
            {
                draftDirty = true;
            }
            bool draftApplied = lineApplied
                && !draftDirty;

            return new BroadcastWorkbenchSnapshot
            {
                selectedLineId = activeLineId,
                lines = runtimeLines.Select(CreateBroadcastWorkbenchLineDto).ToArray(),
                stations = stationGroups
                    .Where(group => group?.Representative != null)
                    .Select(group => CloneDispatchWorkbenchStationDto(group.Representative))
                    .ToArray(),
                turnbackPoints = activeRuntime != null
                    ? BuildBroadcastWorkbenchTurnbackPoints(activeRuntime.Entity, stationGroups)
                    : Array.Empty<BroadcastWorkbenchTurnbackPointDto>(),
                stationBindings = stationBindings,
                rules = rules,
                platformAnnouncements = platformAnnouncements,
                assetDirectory = m_BroadcastAssetDirectory,
                assets = m_BroadcastAssetCatalog.Select(CloneBroadcastWorkbenchAsset).ToArray(),
                version = m_WorkbenchSnapshotVersion.ToString(),
                sourceMode = "game-backend",
                lineApplied = lineApplied,
                lineDraftDirty = lineDraftDirty,
                volumeDirty = volumeDirty,
                draftApplied = draftApplied,
                draftDirty = draftDirty,
                volume = ClampBroadcastVolumePercent(m_BroadcastDraftVolumePercent),
                warnings = activeRuntime != null
                    ? BuildBroadcastWarnings(activeRuntime.Id)
                    : Array.Empty<string>()
            };
        }

        private BroadcastWorkbenchExternalAssetBrowserSnapshot BuildBroadcastAssetBrowserSnapshot(string requestedPath)
        {
            if (string.Equals(requestedPath, BroadcastAssetBrowserDrivesToken, StringComparison.Ordinal))
            {
                return BuildBroadcastAssetBrowserDrivesSnapshot();
            }

            string startPath = ResolveBroadcastAssetBrowserStartPath();
            string currentPath = NormalizeBroadcastAssetDirectory(requestedPath);
            if (string.IsNullOrEmpty(currentPath))
            {
                currentPath = startPath;
            }

            string rootPath = ResolveBroadcastAssetBrowserRootPath(currentPath, startPath);
            string parentPath = ResolveBroadcastAssetBrowserParentPath(currentPath, rootPath);

            List<string> folders = new List<string>();
            List<BroadcastWorkbenchExternalAssetFileDto> files = new List<BroadcastWorkbenchExternalAssetFileDto>();
            string error = string.Empty;

            try
            {
                DirectoryInfo directoryInfo = new DirectoryInfo(currentPath);
                DirectoryInfo[] subdirectories = directoryInfo.GetDirectories();
                for (int i = 0; i < subdirectories.Length; i++)
                {
                    try
                    {
                        if (subdirectories[i].Exists)
                        {
                            folders.Add(subdirectories[i].Name);
                        }
                    }
                    catch
                    {
                    }
                }

                HashSet<string> allowedExtensions = new HashSet<string>(s_BroadcastAssetExtensions, StringComparer.OrdinalIgnoreCase);
                FileInfo[] candidateFiles = directoryInfo.GetFiles();
                for (int i = 0; i < candidateFiles.Length; i++)
                {
                    FileInfo file = candidateFiles[i];
                    string extension = Path.GetExtension(file.FullName);
                    if (string.IsNullOrEmpty(extension) || !allowedExtensions.Contains(extension))
                    {
                        continue;
                    }

                    files.Add(new BroadcastWorkbenchExternalAssetFileDto
                    {
                        id = NormalizeFileBrowserPath(file.FullName),
                        name = file.Name,
                        fullPath = NormalizeFileBrowserPath(file.FullName)
                    });
                }
            }
            catch (Exception ex)
            {
                error = ex.Message ?? string.Empty;
                LogBroadcastWorkbenchException("BuildBroadcastAssetBrowserSnapshot", ex);
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort((left, right) => string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));

            return new BroadcastWorkbenchExternalAssetBrowserSnapshot
            {
                rootPath = rootPath,
                currentPath = currentPath,
                parentPath = parentPath,
                folders = folders.ToArray(),
                files = files.ToArray(),
                allowedExtensions = s_BroadcastAssetExtensions.ToArray(),
                error = error
            };
        }

        private BroadcastWorkbenchExternalAssetBrowserSnapshot BuildBroadcastAssetBrowserDrivesSnapshot()
        {
            List<string> folders = new List<string>();

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].IsReady)
                        {
                            folders.Add(NormalizeFileBrowserPath(drives[i].RootDirectory.FullName));
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                LogBroadcastWorkbenchException("BuildBroadcastAssetBrowserDrivesSnapshot", ex);
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);

            return new BroadcastWorkbenchExternalAssetBrowserSnapshot
            {
                rootPath = string.Empty,
                currentPath = string.Empty,
                parentPath = string.Empty,
                folders = folders.ToArray(),
                files = Array.Empty<BroadcastWorkbenchExternalAssetFileDto>(),
                allowedExtensions = s_BroadcastAssetExtensions.ToArray(),
                error = string.Empty
            };
        }

        private void ApplyBroadcastAssetDirectorySelection(string directory)
        {
            string normalizedDirectory = NormalizeBroadcastAssetDirectory(directory);
            if (string.IsNullOrEmpty(normalizedDirectory))
            {
                return;
            }

            m_BroadcastAssetDirectory = normalizedDirectory;
            m_BroadcastAssetCatalog.Clear();
            m_BroadcastAssetCatalog.AddRange(ScanBroadcastAssetDirectory(normalizedDirectory));

            DispatchWorkbenchEuisBridge.NotifyBroadcastWorkbenchSnapshotChanged(
                BuildBroadcastWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
        }

        private string ResolveBroadcastAssetDirectoryRoot()
        {
            string normalizedDirectory = NormalizeBroadcastAssetDirectory(m_BroadcastAssetDirectory);
            if (!string.IsNullOrEmpty(normalizedDirectory))
            {
                return normalizedDirectory;
            }

            return string.Empty;
        }

        private static string NormalizeBroadcastAssetDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            try
            {
                string fullPath = Path.GetFullPath(directory);
                return Directory.Exists(fullPath) ? NormalizeDirectoryBrowserPath(fullPath) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IEnumerable<BroadcastWorkbenchAssetDto> ScanBroadcastAssetDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<BroadcastWorkbenchAssetDto>();
            }

            HashSet<string> extensions = new HashSet<string>(s_BroadcastAssetExtensions, StringComparer.OrdinalIgnoreCase);
            List<BroadcastWorkbenchAssetDto> assets = new List<BroadcastWorkbenchAssetDto>();

            try
            {
                string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string filePath = files[i];
                    string extension = Path.GetExtension(filePath);
                    if (string.IsNullOrEmpty(extension) || !extensions.Contains(extension))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(filePath);
                    assets.Add(CreateBroadcastWorkbenchAssetDto(filePath));
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("[BroadcastWorkbench] Scan asset directory failed: " + ex.Message);
            }

            assets.Sort((left, right) => string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));
            return assets;
        }

        private string ResolveBroadcastAssetBrowserStartPath()
        {
            string existingDirectory = NormalizeBroadcastAssetDirectory(m_BroadcastExternalBrowseDirectory);
            if (!string.IsNullOrEmpty(existingDirectory))
            {
                return existingDirectory;
            }

            string documentsDirectory = NormalizeBroadcastAssetDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            if (!string.IsNullOrEmpty(documentsDirectory))
            {
                return documentsDirectory;
            }

            string currentDirectory = NormalizeBroadcastAssetDirectory(Environment.CurrentDirectory);
            if (!string.IsNullOrEmpty(currentDirectory))
            {
                return currentDirectory;
            }

            DriveInfo firstReadyDrive = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady);
            if (firstReadyDrive != null)
            {
                return NormalizeFileBrowserPath(firstReadyDrive.RootDirectory.FullName);
            }

            return string.Empty;
        }

        private static string ResolveBroadcastAssetBrowserRootPath(string currentPath, string fallbackPath)
        {
            string rootPath = NormalizeFileBrowserPath(Path.GetPathRoot(currentPath));
            if (!string.IsNullOrEmpty(rootPath))
            {
                return rootPath;
            }

            return NormalizeFileBrowserPath(Path.GetPathRoot(fallbackPath));
        }

        private static string ResolveBroadcastAssetBrowserParentPath(string currentPath, string rootPath)
        {
            string normalizedCurrentPath = NormalizeFileBrowserPath(currentPath);
            string normalizedRootPath = NormalizeFileBrowserPath(rootPath);
            if (string.IsNullOrEmpty(normalizedCurrentPath) || string.IsNullOrEmpty(normalizedRootPath))
            {
                return string.Empty;
            }

            if (string.Equals(normalizedCurrentPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return BroadcastAssetBrowserDrivesToken;
            }

            string currentPathWithoutTrailingSlash = TrimEndingDirectorySeparator(normalizedCurrentPath);
            DirectoryInfo parent = Directory.GetParent(currentPathWithoutTrailingSlash);
            return NormalizeDirectoryBrowserPath(parent?.FullName);
        }

        private static string NormalizeBroadcastAssetFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                return File.Exists(fullPath) ? NormalizeFileBrowserPath(fullPath) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeFileBrowserPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('/', '\\').Trim();
            if (normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            {
                normalized += "\\";
            }

            return normalized;
        }

        private static string NormalizeDirectoryBrowserPath(string path)
        {
            string normalized = NormalizeFileBrowserPath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            return normalized.EndsWith("\\", StringComparison.Ordinal) ? normalized : normalized + "\\";
        }

        private static string TrimEndingDirectorySeparator(string path)
        {
            string normalized = NormalizeFileBrowserPath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            string rootPath = NormalizeFileBrowserPath(Path.GetPathRoot(normalized));
            if (!string.IsNullOrEmpty(rootPath)
                && string.Equals(normalized, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return normalized.TrimEnd('\\');
        }

        private static BroadcastWorkbenchAssetDto CreateBroadcastWorkbenchAssetDto(string filePath)
        {
            string normalizedFilePath = NormalizeBroadcastAssetFilePath(filePath);
            string extension = Path.GetExtension(normalizedFilePath);
            string fileName = Path.GetFileName(normalizedFilePath);
            return new BroadcastWorkbenchAssetDto
            {
                name = fileName ?? string.Empty,
                desc = extension.TrimStart('.').ToUpperInvariant(),
                length = ReadBroadcastAssetLengthDisplay(normalizedFilePath, extension),
                path = normalizedFilePath,
                extension = extension ?? string.Empty
            };
        }

        private static string ReadBroadcastAssetLengthDisplay(string filePath, string extension)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return string.Empty;
            }

            try
            {
                using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Track track = new Track(stream, extension ?? string.Empty);
                return FormatBroadcastAssetLength(track.DurationMs);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatBroadcastAssetLength(double durationMs)
        {
            if (durationMs <= 0)
            {
                return string.Empty;
            }

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);
            if (duration.TotalHours >= 1d)
            {
                return duration.ToString(@"h\:mm\:ss");
            }

            return duration.ToString(@"m\:ss");
        }

        private string EnsureBroadcastManagedAssetDirectory()
        {
            string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLowPath = Path.Combine(Directory.GetParent(localAppDataPath).FullName, "LocalLow");
            string directory = Path.Combine(localLowPath, "Colossal Order", "Cities Skylines II", "ModsData", Mod.Id, BroadcastManagedAssetDirectoryName);
            Directory.CreateDirectory(directory);
            return NormalizeDirectoryBrowserPath(directory);
        }

        private async Task PlayBroadcastAssetPreviewOnMainThreadAsync(string assetName)
        {
            string requestedAssetName = assetName ?? string.Empty;
            BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog.FirstOrDefault(candidate =>
                string.Equals(candidate?.name, requestedAssetName, StringComparison.OrdinalIgnoreCase));
            string assetPath = NormalizeBroadcastAssetFilePath(asset?.path);
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
            {
                StopBroadcastAssetPreviewOnMainThread(requestedAssetName, notify: false);
                NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "error", "Selected asset file was not found.");
                return;
            }

            AudioType audioType = ResolveBroadcastAssetAudioType(assetPath);
            if (audioType == AudioType.UNKNOWN)
            {
                StopBroadcastAssetPreviewOnMainThread(requestedAssetName, notify: false);
                NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "error", "Unsupported audio format.");
                return;
            }

            string previousAssetName = m_BroadcastPreviewAssetName;
            if (!string.IsNullOrEmpty(previousAssetName))
            {
                StopBroadcastAssetPreviewOnMainThread(previousAssetName, notify: true);
            }

            int playbackToken = unchecked(++m_BroadcastPreviewPlaybackToken);
            using UnityWebRequest request = BuildBroadcastPreviewAudioRequest(assetPath, audioType);
            DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
            if (downloadHandler != null)
            {
                downloadHandler.streamAudio = false;
            }

            await request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.ConnectionError
                || request.result == UnityWebRequest.Result.ProtocolError
                || request.result == UnityWebRequest.Result.DataProcessingError)
            {
                NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "error", request.error ?? "Audio preview load failed.");
                return;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null)
            {
                NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "error", "Audio preview load returned no clip.");
                return;
            }

            if (playbackToken != m_BroadcastPreviewPlaybackToken)
            {
                UnityEngine.Object.Destroy(clip);
                return;
            }

            EnsureBroadcastPreviewAudioSource();
            ReleaseBroadcastPreviewAudioClip();
            m_BroadcastPreviewAudioClip = clip;
            m_BroadcastPreviewAudioSource.clip = clip;
            m_BroadcastPreviewAudioSource.loop = false;
            m_BroadcastPreviewAudioSource.pitch = 1f;
            m_BroadcastPreviewAudioSource.volume = ConvertBroadcastVolumePercentToScalar(m_BroadcastDraftVolumePercent);
            m_BroadcastPreviewAudioSource.timeSamples = 0;
            AudioManager.AudioSourcePool.Play(m_BroadcastPreviewAudioSource);
            m_BroadcastPreviewAssetName = requestedAssetName;
            NotifyBroadcastAssetPreviewStateChanged(requestedAssetName, "started", string.Empty);
            MainThreadDispatcher.RegisterUpdater(() => ObserveBroadcastAssetPreviewPlayback(playbackToken, requestedAssetName));
        }

        private void StopBroadcastAssetPreviewOnMainThread(string assetName, bool notify)
        {
            string resolvedAssetName = !string.IsNullOrWhiteSpace(assetName)
                ? assetName
                : m_BroadcastPreviewAssetName;
            unchecked
            {
                m_BroadcastPreviewPlaybackToken++;
            }

            if (m_BroadcastPreviewAudioSource != null)
            {
                AudioManager audioManager = AudioManager.instance;
                if (audioManager != null)
                {
                    audioManager.StopExclusiveUISound(m_BroadcastPreviewAudioSource);
                }
                else
                {
                    AudioManager.AudioSourcePool.Release(m_BroadcastPreviewAudioSource);
                }

                m_BroadcastPreviewAudioSource = null;
            }

            ReleaseBroadcastPreviewAudioClip();
            m_BroadcastPreviewAssetName = string.Empty;

            if (notify && !string.IsNullOrWhiteSpace(resolvedAssetName))
            {
                NotifyBroadcastAssetPreviewStateChanged(resolvedAssetName, "stopped", string.Empty);
            }
        }

        private bool ObserveBroadcastAssetPreviewPlayback(int playbackToken, string assetName)
        {
            if (playbackToken != m_BroadcastPreviewPlaybackToken || m_BroadcastPreviewAudioSource == null)
            {
                return true;
            }

            if (m_BroadcastPreviewAudioSource.isPlaying)
            {
                return false;
            }

            bool wasCurrentAsset = string.Equals(m_BroadcastPreviewAssetName, assetName, StringComparison.OrdinalIgnoreCase);
            StopBroadcastAssetPreviewOnMainThread(assetName, notify: false);
            if (wasCurrentAsset)
            {
                NotifyBroadcastAssetPreviewStateChanged(assetName, "ended", string.Empty);
            }

            return true;
        }

        private void EnsureBroadcastPreviewAudioSource()
        {
            if (m_BroadcastPreviewAudioSource != null)
            {
                return;
            }

            m_BroadcastPreviewAudioSource = AudioManager.AudioSourcePool.Get();
            m_BroadcastPreviewAudioSource.outputAudioMixerGroup = ResolveBroadcastPreviewAudioMixerGroup();
            m_BroadcastPreviewAudioSource.dopplerLevel = 0f;
            m_BroadcastPreviewAudioSource.playOnAwake = false;
            m_BroadcastPreviewAudioSource.spatialBlend = 0f;
            m_BroadcastPreviewAudioSource.ignoreListenerPause = true;
            m_BroadcastPreviewAudioSource.pitch = 1f;
            m_BroadcastPreviewAudioSource.volume = ConvertBroadcastVolumePercentToScalar(m_BroadcastDraftVolumePercent);
        }

        private AudioMixerGroup ResolveBroadcastPreviewAudioMixerGroup()
        {
            AudioManager audioManager = AudioManager.instance;
            if (audioManager == null || s_AudioManagerUiGroupField == null)
            {
                return null;
            }

            try
            {
                return s_AudioManagerUiGroupField.GetValue(audioManager) as AudioMixerGroup;
            }
            catch
            {
                return null;
            }
        }

        private void ReleaseBroadcastPreviewAudioClip()
        {
            if (m_BroadcastPreviewAudioClip == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(m_BroadcastPreviewAudioClip);
            m_BroadcastPreviewAudioClip = null;
        }

        private async Task PlayBroadcastRulePreviewOnMainThreadAsync(string lineId, string ruleId)
        {
            StopBroadcastAssetPreviewOnMainThread(m_BroadcastPreviewAssetName, notify: true);
            StopBroadcastRulePreviewOnMainThread(ruleId, notify: false);

            List<BroadcastWorkbenchRuleDto> rules = BuildBroadcastDraftRules(lineId).ToList();
            BroadcastWorkbenchRuleDto rule = rules.FirstOrDefault(candidate =>
                candidate != null && string.Equals(candidate.id, ruleId, StringComparison.Ordinal));
            if (rule?.nodes == null || rule.nodes.Length == 0)
            {
                NotifyBroadcastRulePreviewStateChanged(ruleId, "error", "Selected rule has no previewable nodes.");
                return;
            }

            if (!TryBuildBroadcastRulePreviewContext(lineId, out BroadcastTriggerContext context))
            {
                NotifyBroadcastRulePreviewStateChanged(ruleId, "error", "Preview context is unavailable.");
                return;
            }

            int playbackToken = unchecked(++m_BroadcastRulePreviewPlaybackToken);
            m_BroadcastPreviewRuleId = ruleId;
            NotifyBroadcastRulePreviewStateChanged(ruleId, "started", string.Empty);

            for (int nodeIndex = 0; nodeIndex < rule.nodes.Length; nodeIndex++)
            {
                if (playbackToken != m_BroadcastRulePreviewPlaybackToken)
                {
                    return;
                }

                BroadcastWorkbenchRuleNodeDto node = rule.nodes[nodeIndex];
                if (node == null)
                {
                    continue;
                }

                if (string.Equals(node.type, "delay", StringComparison.Ordinal))
                {
                    float delaySeconds = node.delaySeconds > 0f ? node.delaySeconds : 0f;
                    if (delaySeconds > 0)
                    {
                        await Task.Delay(Mathf.Max(1, Mathf.RoundToInt(delaySeconds * 1000f)));
                    }

                    continue;
                }

                string assetName = ResolveBroadcastRuntimeAssetName(node, context);
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    continue;
                }

                AudioClip clip = await LoadBroadcastPreviewClipAsync(assetName);
                if (playbackToken != m_BroadcastRulePreviewPlaybackToken)
                {
                    DestroyBroadcastPreviewClip(clip);
                    return;
                }

                if (clip == null)
                {
                    continue;
                }

                EnsureBroadcastRulePreviewAudioSource();
                ReleaseBroadcastRulePreviewAudioClip();
                m_BroadcastRulePreviewAudioClip = clip;
                m_BroadcastRulePreviewAudioSource.clip = clip;
                m_BroadcastRulePreviewAudioSource.loop = false;
                m_BroadcastRulePreviewAudioSource.pitch = 1f;
                m_BroadcastRulePreviewAudioSource.volume = ConvertBroadcastVolumePercentToScalar(m_BroadcastDraftVolumePercent);
                m_BroadcastRulePreviewAudioSource.timeSamples = 0;
                AudioManager.AudioSourcePool.Play(m_BroadcastRulePreviewAudioSource);
                await Task.Delay(Mathf.Max(1, Mathf.RoundToInt(clip.length * 1000f)));
            }

            if (playbackToken != m_BroadcastRulePreviewPlaybackToken)
            {
                return;
            }

            StopBroadcastRulePreviewOnMainThread(ruleId, notify: false);
            NotifyBroadcastRulePreviewStateChanged(ruleId, "ended", string.Empty);
        }

        private async Task<AudioClip> LoadBroadcastPreviewClipAsync(string assetName)
        {
            string requestedAssetName = assetName ?? string.Empty;
            BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog.FirstOrDefault(candidate =>
                string.Equals(candidate?.name, requestedAssetName, StringComparison.OrdinalIgnoreCase));
            string assetPath = NormalizeBroadcastAssetFilePath(asset?.path);
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
            {
                return null;
            }

            AudioType audioType = ResolveBroadcastAssetAudioType(assetPath);
            if (audioType == AudioType.UNKNOWN)
            {
                return null;
            }

            return await RunBroadcastMainThreadTaskAsync(async () =>
            {
                using UnityWebRequest request = BuildBroadcastPreviewAudioRequest(assetPath, audioType);
                DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
                if (downloadHandler != null)
                {
                    downloadHandler.streamAudio = false;
                }

                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.ConnectionError
                    || request.result == UnityWebRequest.Result.ProtocolError
                    || request.result == UnityWebRequest.Result.DataProcessingError)
                {
                    return null;
                }

                return DownloadHandlerAudioClip.GetContent(request);
            });
        }

        private void StopBroadcastRulePreviewOnMainThread(string ruleId, bool notify)
        {
            string resolvedRuleId = !string.IsNullOrWhiteSpace(ruleId)
                ? ruleId
                : m_BroadcastPreviewRuleId;
            unchecked
            {
                m_BroadcastRulePreviewPlaybackToken++;
            }

            if (m_BroadcastRulePreviewAudioSource != null)
            {
                AudioManager audioManager = AudioManager.instance;
                if (audioManager != null)
                {
                    audioManager.StopExclusiveUISound(m_BroadcastRulePreviewAudioSource);
                }
                else
                {
                    AudioManager.AudioSourcePool.Release(m_BroadcastRulePreviewAudioSource);
                }

                m_BroadcastRulePreviewAudioSource = null;
            }

            ReleaseBroadcastRulePreviewAudioClip();
            m_BroadcastPreviewRuleId = string.Empty;

            if (notify && !string.IsNullOrWhiteSpace(resolvedRuleId))
            {
                NotifyBroadcastRulePreviewStateChanged(resolvedRuleId, "stopped", string.Empty);
            }
        }

        private void EnsureBroadcastRulePreviewAudioSource()
        {
            if (m_BroadcastRulePreviewAudioSource != null)
            {
                return;
            }

            m_BroadcastRulePreviewAudioSource = AudioManager.AudioSourcePool.Get();
            m_BroadcastRulePreviewAudioSource.outputAudioMixerGroup = ResolveBroadcastPreviewAudioMixerGroup();
            m_BroadcastRulePreviewAudioSource.dopplerLevel = 0f;
            m_BroadcastRulePreviewAudioSource.playOnAwake = false;
            m_BroadcastRulePreviewAudioSource.spatialBlend = 0f;
            m_BroadcastRulePreviewAudioSource.ignoreListenerPause = true;
            m_BroadcastRulePreviewAudioSource.pitch = 1f;
            m_BroadcastRulePreviewAudioSource.volume = ConvertBroadcastVolumePercentToScalar(m_BroadcastDraftVolumePercent);
        }

        private void ReleaseBroadcastRulePreviewAudioClip()
        {
            if (m_BroadcastRulePreviewAudioClip == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(m_BroadcastRulePreviewAudioClip);
            m_BroadcastRulePreviewAudioClip = null;
        }

        private static void DestroyBroadcastPreviewClip(AudioClip clip)
        {
            if (clip == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(clip);
        }

        private void NotifyBroadcastRulePreviewStateChanged(string ruleId, string state, string error)
        {
            DispatchWorkbenchEuisBridge.NotifyBroadcastRulePreviewStateChanged(new BroadcastWorkbenchRulePreviewStateDto
            {
                ruleId = ruleId ?? string.Empty,
                state = state ?? string.Empty,
                error = error ?? string.Empty
            });
        }

        private bool TryBuildBroadcastRulePreviewContext(string lineId, out BroadcastTriggerContext context)
        {
            context = default;
            WorkbenchLineRuntime runtime = ResolveBroadcastWorkbenchLineRuntime(lineId);
            if (runtime == null)
            {
                return false;
            }

            List<BroadcastWorkbenchStationGroup> stationGroups;
            EnsureBroadcastLineStateMigrated(lineId, runtime.Entity, out stationGroups);
            if (stationGroups.Count == 0)
            {
                return false;
            }

            BroadcastWorkbenchStationGroup currentStation = stationGroups[0];
            BroadcastWorkbenchStationGroup nextStation = stationGroups.Count > 1 ? stationGroups[1] : null;
            BroadcastWorkbenchStationGroup terminalStation = stationGroups[0];
            BroadcastWorkbenchStationGroup turnbackStation =
                TryResolveBroadcastWorkbenchTurnbackStation(runtime.Entity, stationGroups, out BroadcastWorkbenchStationGroup resolvedTurnbackStation)
                    ? resolvedTurnbackStation
                    : null;
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastDraftLineStationBindingsOrNull(lineId);
            List<BroadcastWorkbenchStationBindingDto> currentStationBindings = ResolveBroadcastBoundBindings(lineBindings, currentStation?.Representative?.id);
            List<BroadcastWorkbenchStationBindingDto> nextStationBindings = ResolveBroadcastBoundBindings(lineBindings, nextStation?.Representative?.id);
            List<BroadcastWorkbenchStationBindingDto> terminalStationBindings = ResolveBroadcastBoundBindings(lineBindings, terminalStation?.Representative?.id);
            List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings = ResolveBroadcastBoundBindings(lineBindings, turnbackStation?.Representative?.id);
            context = new BroadcastTriggerContext(
                lineId,
                Entity.Null,
                currentStation?.Representative?.name ?? string.Empty,
                nextStation?.Representative?.name ?? string.Empty,
                terminalStation?.Representative?.name ?? string.Empty,
                turnbackStation?.Representative?.name ?? string.Empty,
                ResolveBroadcastBoundAssetName(currentStationBindings, 1),
                ResolveBroadcastBoundAssetName(nextStationBindings, 1),
                ResolveBroadcastBoundAssetName(terminalStationBindings, 1),
                ResolveBroadcastBoundAssetName(turnbackStationBindings, 1),
                currentStationBindings,
                nextStationBindings,
                terminalStationBindings,
                turnbackStationBindings);
            return true;
        }

        private bool TryResolveBroadcastWorkbenchTurnbackStation(
            Entity line,
            List<BroadcastWorkbenchStationGroup> stationGroups,
            out BroadcastWorkbenchStationGroup stationGroup)
        {
            stationGroup = null;
            if (line == Entity.Null
                || stationGroups == null
                || stationGroups.Count == 0
                || !EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
            if (!TryGetBroadcastLineStationContextCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.TurnbackStations == null
                || cache.TurnbackStations.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                BroadcastResolvedStation turnbackStation = cache.TurnbackStations[i];
                if (turnbackStation != null
                    && TryFindBroadcastWorkbenchStationGroup(stationGroups, turnbackStation.StationId, out stationGroup))
                {
                    return true;
                }
            }

            return false;
        }

        private BroadcastWorkbenchTurnbackPointDto[] BuildBroadcastWorkbenchTurnbackPoints(
            Entity line,
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            if (line == Entity.Null
                || stationGroups == null
                || !EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line))
            {
                return Array.Empty<BroadcastWorkbenchTurnbackPointDto>();
            }

            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
            if (!TryGetBroadcastLineStationContextCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.TurnbackStations == null)
            {
                return Array.Empty<BroadcastWorkbenchTurnbackPointDto>();
            }

            List<BroadcastWorkbenchTurnbackPointDto> points = new List<BroadcastWorkbenchTurnbackPointDto>();
            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                BroadcastResolvedStation turnbackStation = cache.TurnbackStations[i];
                BroadcastWorkbenchStationGroup stationGroup = null;
                bool resolved = turnbackStation != null
                    && TryFindBroadcastWorkbenchStationGroup(
                        stationGroups,
                        turnbackStation.StationId,
                        out stationGroup);
                points.Add(new BroadcastWorkbenchTurnbackPointDto
                {
                    index = points.Count + 1,
                    stationId = resolved
                        ? stationGroup?.Representative?.id ?? string.Empty
                        : turnbackStation?.StationId ?? string.Empty,
                    stationName = resolved
                        ? stationGroup?.Representative?.name ?? string.Empty
                        : turnbackStation?.Name ?? string.Empty,
                    resolved = resolved
                });
            }

            BroadcastWorkbenchStationGroup terminalStationGroup =
                stationGroups.Count > 0 ? stationGroups[0] : null;
            bool hasResolvedTurnback = points.Any(point => point != null && point.resolved);
            bool terminalAlreadyIncluded = terminalStationGroup != null
                && points.Any(point => point != null
                    && point.resolved
                    && string.Equals(point.stationId, terminalStationGroup.Representative?.id ?? string.Empty, StringComparison.Ordinal));
            if (hasResolvedTurnback
                && terminalStationGroup?.Representative != null
                && !terminalAlreadyIncluded)
            {
                points.Add(new BroadcastWorkbenchTurnbackPointDto
                {
                    index = points.Count + 1,
                    stationId = terminalStationGroup.Representative.id ?? string.Empty,
                    stationName = terminalStationGroup.Representative.name ?? string.Empty,
                    resolved = true
                });
            }

            return points.ToArray();
        }

        private static bool TryFindBroadcastWorkbenchStationGroup(
            List<BroadcastWorkbenchStationGroup> stationGroups,
            string stationId,
            out BroadcastWorkbenchStationGroup stationGroup)
        {
            stationGroup = null;
            if (stationGroups == null || stationGroups.Count == 0 || string.IsNullOrWhiteSpace(stationId))
            {
                return false;
            }

            stationGroup = stationGroups.FirstOrDefault(group =>
                group != null
                && string.Equals(group.Representative?.id ?? string.Empty, stationId, StringComparison.Ordinal));
            return stationGroup != null;
        }

        private void ApplyBroadcastPreviewVolumeToActivePreviewSources()
        {
            float volume = ConvertBroadcastVolumePercentToScalar(m_BroadcastDraftVolumePercent);
            if (m_BroadcastPreviewAudioSource != null)
            {
                m_BroadcastPreviewAudioSource.volume = volume;
            }

            if (m_BroadcastRulePreviewAudioSource != null)
            {
                m_BroadcastRulePreviewAudioSource.volume = volume;
            }
        }

        private static float ConvertBroadcastVolumePercentToScalar(int volumePercent)
        {
            float progress = ClampBroadcastVolumePercent(volumePercent) / 100f;
            return Mathf.Lerp(BroadcastVolumeScalarMin, BroadcastVolumeScalarMax, progress);
        }

        private static int ClampBroadcastVolumePercent(int volumePercent)
        {
            return Mathf.Clamp(volumePercent, 0, 100);
        }

        private static int ParseBroadcastVolumePercent(string volumeJson, int fallback)
        {
            if (!int.TryParse(volumeJson ?? string.Empty, out int parsed))
            {
                return ClampBroadcastVolumePercent(fallback);
            }

            return ClampBroadcastVolumePercent(parsed);
        }

        private void NotifyBroadcastAssetPreviewStateChanged(string assetName, string state, string error)
        {
            DispatchWorkbenchEuisBridge.NotifyBroadcastAssetPreviewStateChanged(new BroadcastWorkbenchAssetPreviewStateDto
            {
                assetName = assetName ?? string.Empty,
                state = state ?? string.Empty,
                error = error ?? string.Empty
            });
        }

        private static UnityWebRequest BuildBroadcastPreviewAudioRequest(string path, AudioType audioType)
        {
            if (path.StartsWith("//?/", StringComparison.Ordinal))
            {
                return UnityWebRequestMultimedia.GetAudioClip("file://" + path.Replace("/", "\\"), audioType);
            }

            return UnityWebRequestMultimedia.GetAudioClip(new Uri("file://" + path), audioType);
        }

        private static AudioType ResolveBroadcastAssetAudioType(string filePath)
        {
            switch ((Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant())
            {
                case ".ogg":
                    return AudioType.OGGVORBIS;
                case ".wav":
                    return AudioType.WAV;
                case ".mp3":
                    return AudioType.MPEG;
                default:
                    return AudioType.UNKNOWN;
            }
        }

        private Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> GetOrCreateBroadcastDraftLineStationBindings(string lineId)
        {
            string lineKey = lineId ?? string.Empty;
            if (!m_BroadcastDraftLineStationAssetBindings.TryGetValue(lineKey, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings))
            {
                lineBindings = new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                m_BroadcastDraftLineStationAssetBindings[lineKey] = lineBindings;
            }

            return lineBindings;
        }

        private Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> GetBroadcastAppliedLineStationBindings(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                || lineBindings == null)
            {
                return null;
            }

            return lineBindings;
        }

        private Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> GetBroadcastDraftLineStationBindingsOrNull(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastDraftLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                || lineBindings == null)
            {
                return null;
            }

            return lineBindings;
        }

        private List<BroadcastWorkbenchRuleDto> GetBroadcastAppliedLineRules(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastLineRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                || rules == null)
            {
                return null;
            }

            return rules;
        }

        private List<BroadcastWorkbenchRuleDto> GetBroadcastDraftLineRulesOrNull(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastDraftLineRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                || rules == null)
            {
                return null;
            }

            return rules;
        }

        private Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> GetOrCreateBroadcastDraftLinePlatformAnnouncements(string lineId)
        {
            string lineKey = lineId ?? string.Empty;
            if (!m_BroadcastDraftLinePlatformAnnouncements.TryGetValue(lineKey, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements))
            {
                announcements = new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
                m_BroadcastDraftLinePlatformAnnouncements[lineKey] = announcements;
            }

            return announcements;
        }

        private Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> GetBroadcastAppliedLinePlatformAnnouncements(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements)
                || announcements == null)
            {
                return null;
            }

            return announcements;
        }

        private Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> GetBroadcastDraftLinePlatformAnnouncementsOrNull(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastDraftLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements)
                || announcements == null)
            {
                return null;
            }

            return announcements;
        }

        private WorkbenchLineRuntime ResolveBroadcastWorkbenchLineRuntime(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return null;
            }

            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            return runtimeLines.FirstOrDefault(runtime =>
                runtime != null && string.Equals(runtime.Id, lineId, StringComparison.Ordinal));
        }

        private bool EnsureBroadcastLineStateMigrated(
            string lineId,
            Entity line,
            out List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            stationGroups = BuildBroadcastWorkbenchStationGroups(line);
            if (string.IsNullOrWhiteSpace(lineId) || line == Entity.Null || stationGroups.Count == 0)
            {
                return false;
            }

            bool changed = false;
            changed |= MigrateBroadcastLineBindings(
                lineId,
                stationGroups,
                m_BroadcastDraftLineStationAssetBindings,
                m_BroadcastDraftLegacyLineStationAssetBindings);
            changed |= MigrateBroadcastLineBindings(
                lineId,
                stationGroups,
                m_BroadcastLineStationAssetBindings,
                m_BroadcastLegacyLineStationAssetBindings);
            changed |= MigrateBroadcastLinePlatformAnnouncements(
                lineId,
                stationGroups,
                m_BroadcastDraftLinePlatformAnnouncements,
                m_BroadcastDraftLegacyLinePlatformAnnouncements);
            changed |= MigrateBroadcastLinePlatformAnnouncements(
                lineId,
                stationGroups,
                m_BroadcastLinePlatformAnnouncements,
                m_BroadcastLegacyLinePlatformAnnouncements);
            return changed;
        }

        private static Dictionary<string, string> BuildBroadcastAnchorKeyByLegacyStationId(
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            Dictionary<string, string> anchorKeyByLegacyStationId =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (stationGroups == null)
            {
                return anchorKeyByLegacyStationId;
            }

            for (int i = 0; i < stationGroups.Count; i++)
            {
                BroadcastWorkbenchStationGroup group = stationGroups[i];
                string anchorKey = group?.Representative?.id ?? string.Empty;
                if (string.IsNullOrWhiteSpace(anchorKey) || group?.StationIds == null)
                {
                    continue;
                }

                for (int j = 0; j < group.StationIds.Count; j++)
                {
                    string legacyId = group.StationIds[j];
                    if (!string.IsNullOrWhiteSpace(legacyId))
                    {
                        anchorKeyByLegacyStationId[legacyId] = anchorKey;
                    }
                }
            }

            return anchorKeyByLegacyStationId;
        }

        private static Dictionary<string, string> BuildBroadcastStationNameByAnchorKey(
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            Dictionary<string, string> stationNameByAnchorKey =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (stationGroups == null)
            {
                return stationNameByAnchorKey;
            }

            for (int i = 0; i < stationGroups.Count; i++)
            {
                BroadcastWorkbenchStationGroup group = stationGroups[i];
                string anchorKey = group?.Representative?.id ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(anchorKey))
                {
                    stationNameByAnchorKey[anchorKey] = group?.Representative?.name ?? string.Empty;
                }
            }

            return stationNameByAnchorKey;
        }

        private static string ResolveBroadcastAnchorStationId(
            string stationId,
            HashSet<string> validAnchorIds,
            Dictionary<string, string> anchorKeyByLegacyStationId)
        {
            string candidate = stationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            if (validAnchorIds != null && validAnchorIds.Contains(candidate))
            {
                return candidate;
            }

            if (anchorKeyByLegacyStationId != null
                && anchorKeyByLegacyStationId.TryGetValue(candidate, out string mappedAnchorKey)
                && !string.IsNullOrWhiteSpace(mappedAnchorKey))
            {
                return mappedAnchorKey;
            }

            return string.Empty;
        }

        private static void AddBroadcastBindingRange(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> target,
            string stationId,
            IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
        {
            if (target == null || string.IsNullOrWhiteSpace(stationId) || bindings == null)
            {
                return;
            }

            if (!target.TryGetValue(stationId, out List<BroadcastWorkbenchStationBindingDto> current))
            {
                current = new List<BroadcastWorkbenchStationBindingDto>();
                target[stationId] = current;
            }

            foreach (BroadcastWorkbenchStationBindingDto binding in bindings)
            {
                if (binding != null && !string.IsNullOrWhiteSpace(binding.assetName))
                {
                    current.Add(binding);
                }
            }
        }

        private bool MigrateBroadcastLineBindings(
            string lineId,
            List<BroadcastWorkbenchStationGroup> stationGroups,
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source,
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> legacySource)
        {
            source.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> currentMain);
            legacySource.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> currentLegacy);
            if ((currentMain == null || currentMain.Count == 0)
                && (currentLegacy == null || currentLegacy.Count == 0))
            {
                return false;
            }

            Dictionary<string, string> anchorKeyByLegacyStationId =
                BuildBroadcastAnchorKeyByLegacyStationId(stationGroups);
            HashSet<string> validAnchorIds = new HashSet<string>(
                stationGroups
                    .Select(group => group?.Representative?.id ?? string.Empty)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> nextMain =
                new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> nextLegacy =
                new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);

            void Consume(Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> bindingsByStationId)
            {
                if (bindingsByStationId == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> entry in bindingsByStationId)
                {
                    List<BroadcastWorkbenchStationBindingDto> normalizedBindings =
                        NormalizeBroadcastStationBindings(entry.Key, entry.Value);
                    if (normalizedBindings.Count == 0)
                    {
                        continue;
                    }

                    string mappedStationId = ResolveBroadcastAnchorStationId(
                        entry.Key,
                        validAnchorIds,
                        anchorKeyByLegacyStationId);
                    if (!string.IsNullOrWhiteSpace(mappedStationId))
                    {
                        if (nextMain.ContainsKey(mappedStationId))
                        {
                            continue;
                        }

                        AddBroadcastBindingRange(nextMain, mappedStationId, normalizedBindings);
                    }
                    else
                    {
                        nextLegacy[entry.Key] = normalizedBindings;
                    }
                }
            }

            Consume(currentMain);
            Consume(currentLegacy);

            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> normalizedMain = nextMain.Count == 0
                ? null
                : nextMain
                    .ToDictionary(
                        entry => entry.Key,
                        entry => NormalizeBroadcastStationBindings(entry.Key, entry.Value),
                        StringComparer.Ordinal);
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> normalizedLegacy = nextLegacy.Count == 0
                ? null
                : nextLegacy
                    .ToDictionary(
                        entry => entry.Key,
                        entry => NormalizeBroadcastStationBindings(entry.Key, entry.Value),
                        StringComparer.Ordinal);

            bool changed = !AreBroadcastLineBindingsEqual(currentMain, normalizedMain)
                || !AreBroadcastLineBindingsEqual(currentLegacy, normalizedLegacy);
            if (!changed)
            {
                return false;
            }

            if (normalizedMain == null || normalizedMain.Count == 0)
            {
                source.Remove(lineId);
            }
            else
            {
                source[lineId] = normalizedMain;
            }

            if (normalizedLegacy == null || normalizedLegacy.Count == 0)
            {
                legacySource.Remove(lineId);
            }
            else
            {
                legacySource[lineId] = normalizedLegacy;
            }

            return true;
        }

        private bool MigrateBroadcastLinePlatformAnnouncements(
            string lineId,
            List<BroadcastWorkbenchStationGroup> stationGroups,
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source,
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> legacySource)
        {
            source.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> currentMain);
            legacySource.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> currentLegacy);
            if ((currentMain == null || currentMain.Count == 0)
                && (currentLegacy == null || currentLegacy.Count == 0))
            {
                return false;
            }

            Dictionary<string, string> anchorKeyByLegacyStationId =
                BuildBroadcastAnchorKeyByLegacyStationId(stationGroups);
            Dictionary<string, string> stationNameByAnchorKey =
                BuildBroadcastStationNameByAnchorKey(stationGroups);
            HashSet<string> validAnchorIds = new HashSet<string>(
                stationNameByAnchorKey.Keys,
                StringComparer.Ordinal);
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> nextMain =
                new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> nextLegacy =
                new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);

            void Consume(Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcementsByKey)
            {
                if (announcementsByKey == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in announcementsByKey)
                {
                    BroadcastWorkbenchPlatformAnnouncementDto announcement = entry.Value;
                    if (announcement == null || string.IsNullOrWhiteSpace(announcement.stationId))
                    {
                        continue;
                    }

                    string mappedStationId = ResolveBroadcastAnchorStationId(
                        announcement.stationId,
                        validAnchorIds,
                        anchorKeyByLegacyStationId);
                    if (!string.IsNullOrWhiteSpace(mappedStationId))
                    {
                        if (nextMain.ContainsKey(BuildBroadcastPlatformAnnouncementStorageKey(mappedStationId, announcement.uiTriggerId)))
                        {
                            continue;
                        }

                        string stationName = stationNameByAnchorKey.TryGetValue(mappedStationId, out string mappedName)
                            ? mappedName
                            : announcement.stationName;
                        BroadcastWorkbenchPlatformAnnouncementDto normalized = CloneBroadcastPlatformAnnouncement(
                            announcement,
                            lineId,
                            mappedStationId,
                            stationName);
                        string storageKey = BuildBroadcastPlatformAnnouncementStorageKey(
                            normalized.stationId,
                            normalized.uiTriggerId);
                        if (!string.IsNullOrWhiteSpace(storageKey))
                        {
                            nextMain[storageKey] = normalized;
                        }
                    }
                    else
                    {
                        BroadcastWorkbenchPlatformAnnouncementDto legacyAnnouncement = CloneBroadcastPlatformAnnouncement(
                            announcement,
                            lineId,
                            announcement.stationId,
                            announcement.stationName);
                        string storageKey = BuildBroadcastPlatformAnnouncementStorageKey(
                            legacyAnnouncement.stationId,
                            legacyAnnouncement.uiTriggerId);
                        if (!string.IsNullOrWhiteSpace(storageKey))
                        {
                            nextLegacy[storageKey] = legacyAnnouncement;
                        }
                    }
                }
            }

            Consume(currentMain);
            Consume(currentLegacy);

            bool changed = !AreBroadcastPlatformAnnouncementsEqual(currentMain, nextMain)
                || !AreBroadcastPlatformAnnouncementsEqual(currentLegacy, nextLegacy);
            if (!changed)
            {
                return false;
            }

            if (nextMain.Count == 0)
            {
                source.Remove(lineId);
            }
            else
            {
                source[lineId] = nextMain;
            }

            if (nextLegacy.Count == 0)
            {
                legacySource.Remove(lineId);
            }
            else
            {
                legacySource[lineId] = nextLegacy;
            }

            return true;
        }

        private List<BroadcastWorkbenchStationGroup> BuildBroadcastWorkbenchStationGroups(Entity line)
        {
            List<BroadcastWorkbenchStationGroup> groups = new List<BroadcastWorkbenchStationGroup>();
            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return groups;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (!TryGetBroadcastLineStationContextCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0)
            {
                return groups;
            }

            Dictionary<string, BroadcastWorkbenchStationGroup> groupsByKey =
                new Dictionary<string, BroadcastWorkbenchStationGroup>(StringComparer.Ordinal);

            for (int i = 0; i < cache.Stations.Length; i++)
            {
                BroadcastResolvedStation station = cache.Stations[i];
                if (station == null || string.IsNullOrWhiteSpace(station.StationId))
                {
                    continue;
                }

                string key = station.StationId;

                if (!groupsByKey.TryGetValue(key, out BroadcastWorkbenchStationGroup group))
                {
                    group = new BroadcastWorkbenchStationGroup
                    {
                        Key = key,
                        AnchorEntity = station.AnchorEntity,
                        Representative = new DispatchWorkbenchStationDto
                        {
                            id = station.StationId,
                            name = station.Name ?? string.Empty,
                            order = groups.Count,
                            distance = 0f,
                            hasSiding = false,
                            conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>()
                        }
                    };
                    groupsByKey[key] = group;
                    groups.Add(group);
                }

                if (!string.IsNullOrWhiteSpace(station.LegacyStationId))
                {
                    group.StationIds.Add(station.LegacyStationId);
                }
            }

            return groups;
        }

        private static DispatchWorkbenchStationDto CloneDispatchWorkbenchStationDto(DispatchWorkbenchStationDto station)
        {
            if (station == null)
            {
                return null;
            }

            return new DispatchWorkbenchStationDto
            {
                id = station.id ?? string.Empty,
                name = station.name ?? string.Empty,
                order = station.order,
                distance = station.distance,
                hasSiding = station.hasSiding,
                conflictAssets = station.conflictAssets == null
                    ? null
                    : station.conflictAssets
                        .Select(conflict => conflict == null
                            ? null
                            : new DispatchWorkbenchStationConflictDto
                            {
                                assetName = conflict.assetName ?? string.Empty,
                                suggestedLang = conflict.suggestedLang ?? string.Empty
                            })
                        .Where(conflict => conflict != null)
                        .ToArray()
            };
        }

        private static List<BroadcastWorkbenchStationBindingDto> NormalizeBroadcastStationBindings(
            string stationId,
            IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
        {
            List<BroadcastWorkbenchStationBindingDto> normalized = new List<BroadcastWorkbenchStationBindingDto>();
            if (bindings == null)
            {
                return normalized;
            }

            int nextIndex = 1;
            foreach (BroadcastWorkbenchStationBindingDto binding in bindings)
            {
                string assetName = binding?.assetName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(assetName))
                {
                    continue;
                }

                normalized.Add(new BroadcastWorkbenchStationBindingDto
                {
                    stationId = stationId ?? binding?.stationId ?? string.Empty,
                    lang = NormalizeBroadcastBindingLabel(binding?.lang, nextIndex),
                    langIndex = nextIndex,
                    assetName = assetName
                });
                nextIndex++;
            }

            return normalized;
        }

        private static List<BroadcastWorkbenchStationBindingDto> CloneBroadcastStationBindings(
            string stationId,
            IEnumerable<BroadcastWorkbenchStationBindingDto> bindings)
        {
            return NormalizeBroadcastStationBindings(stationId, bindings);
        }

        private static Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> CloneBroadcastLineBindings(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> source)
        {
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> clone =
                new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
            if (source == null)
            {
                return clone;
            }

            foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> entry in source)
            {
                List<BroadcastWorkbenchStationBindingDto> bindings = CloneBroadcastStationBindings(entry.Key, entry.Value);
                if (bindings.Count > 0)
                {
                    clone[entry.Key] = bindings;
                }
            }

            return clone;
        }

        private static string NormalizeBroadcastBindingLabel(string lang, int langIndex)
        {
            string normalized = (lang ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            return string.Equals(normalized, langIndex.ToString(), StringComparison.Ordinal)
                ? string.Empty
                : normalized;
        }

        private static BroadcastWorkbenchStationBindingDto[] FlattenBroadcastLineBindings(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
        {
            if (lineBindings == null || lineBindings.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchStationBindingDto>();
            }

            return lineBindings
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => CloneBroadcastStationBindings(entry.Key, entry.Value))
                .ToArray();
        }

        private static bool TryGetBroadcastPrimaryBindingAssetName(
            List<BroadcastWorkbenchStationBindingDto> bindings,
            out string assetName)
        {
            assetName = bindings?
                .OrderBy(binding => binding?.langIndex ?? int.MaxValue)
                .Select(binding => binding?.assetName ?? string.Empty)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;
            return !string.IsNullOrEmpty(assetName);
        }

        private BroadcastWorkbenchBindingSlotHintDto[] BuildBroadcastBindingSlotHints(string lineId)
        {
            Dictionary<int, HashSet<string>> labelsBySlot =
                new Dictionary<int, HashSet<string>>();
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastDraftLineStationBindingsOrNull(lineId);
            if (lineBindings == null || lineBindings.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchBindingSlotHintDto>();
            }

            foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> stationEntry in lineBindings)
            {
                List<BroadcastWorkbenchStationBindingDto> bindings = stationEntry.Value;
                if (bindings == null || bindings.Count == 0)
                {
                    continue;
                }

                int compactSlotIndex = 1;
                foreach (BroadcastWorkbenchStationBindingDto binding in bindings
                    .Where(binding => binding != null && !string.IsNullOrWhiteSpace(binding.assetName))
                    .OrderBy(binding => binding.langIndex > 0 ? binding.langIndex : int.MaxValue))
                {
                    string label = (binding.lang ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(label))
                    {
                        compactSlotIndex++;
                        continue;
                    }

                    if (!labelsBySlot.TryGetValue(compactSlotIndex, out HashSet<string> labels))
                    {
                        labels = new HashSet<string>(StringComparer.Ordinal);
                        labelsBySlot[compactSlotIndex] = labels;
                    }

                    labels.Add(label);
                    compactSlotIndex++;
                }
            }

            return labelsBySlot
                .OrderBy(entry => entry.Key)
                .Select(entry => new BroadcastWorkbenchBindingSlotHintDto
                {
                    langIndex = entry.Key,
                    labels = entry.Value
                        .OrderBy(label => label, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray();
        }

        private BroadcastWorkbenchStationBindingDto[] BuildBroadcastDraftStationBindings(
            string lineId,
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastDraftLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings)
                || lineBindings == null
                || lineBindings.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchStationBindingDto>();
            }

            if (stationGroups == null || stationGroups.Count == 0)
            {
                return lineBindings
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .SelectMany(entry => CloneBroadcastStationBindings(entry.Key, entry.Value))
                    .ToArray();
            }

            HashSet<string> validStationIds = new HashSet<string>(
                stationGroups
                    .Select(group => group?.Representative?.id ?? string.Empty)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            return lineBindings
                .Where(entry => validStationIds.Contains(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => CloneBroadcastStationBindings(entry.Key, entry.Value))
                .ToArray();
        }

        private BroadcastWorkbenchRuleDto[] BuildBroadcastDraftRules(string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !m_BroadcastDraftLineRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                || rules == null
                || rules.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchRuleDto>();
            }

            return rules
                .Select(CloneBroadcastWorkbenchRule)
                .Where(rule => rule != null)
                .ToArray();
        }

        private BroadcastWorkbenchPlatformAnnouncementDto[] BuildBroadcastDraftPlatformAnnouncements(
            string lineId,
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements =
                GetBroadcastDraftLinePlatformAnnouncementsOrNull(lineId);
            if (string.IsNullOrEmpty(lineId)
                || stationGroups == null
                || stationGroups.Count == 0
                || announcements == null
                || announcements.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
            }

            Dictionary<string, string> stationNameByAnchorKey =
                BuildBroadcastStationNameByAnchorKey(stationGroups);

            List<BroadcastWorkbenchPlatformAnnouncementDto> result = new List<BroadcastWorkbenchPlatformAnnouncementDto>();
            foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in announcements)
            {
                BroadcastWorkbenchPlatformAnnouncementDto announcement = entry.Value;
                if (announcement == null || string.IsNullOrWhiteSpace(announcement.stationId))
                {
                    continue;
                }

                if (!stationNameByAnchorKey.TryGetValue(announcement.stationId, out string stationName))
                {
                    continue;
                }

                result.Add(CloneBroadcastPlatformAnnouncement(
                    announcement,
                    lineId,
                    announcement.stationId,
                    stationName));
            }

            return result.ToArray();
        }

        private void ApplyBroadcastDraftToRuntime(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return;
            }

            if (m_BroadcastDraftLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftBindings)
                && draftBindings != null
                && draftBindings.Count > 0)
            {
                m_BroadcastLineStationAssetBindings[lineId] = CloneBroadcastLineBindings(draftBindings);
            }
            else
            {
                m_BroadcastLineStationAssetBindings.Remove(lineId);
            }

            if (m_BroadcastDraftLegacyLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftLegacyBindings)
                && draftLegacyBindings != null
                && draftLegacyBindings.Count > 0)
            {
                m_BroadcastLegacyLineStationAssetBindings[lineId] = CloneBroadcastLineBindings(draftLegacyBindings);
            }
            else
            {
                m_BroadcastLegacyLineStationAssetBindings.Remove(lineId);
            }

            if (m_BroadcastDraftLineRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> draftRules)
                && draftRules != null
                && draftRules.Count > 0)
            {
                m_BroadcastLineRules[lineId] = draftRules
                    .Select(CloneBroadcastWorkbenchRule)
                    .Where(rule => rule != null)
                    .ToList();
            }
            else
            {
                m_BroadcastLineRules.Remove(lineId);
            }

            if (m_BroadcastDraftLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftAnnouncements)
                && draftAnnouncements != null
                && draftAnnouncements.Count > 0)
            {
                m_BroadcastLinePlatformAnnouncements[lineId] = CloneBroadcastLinePlatformAnnouncements(draftAnnouncements);
            }
            else
            {
                m_BroadcastLinePlatformAnnouncements.Remove(lineId);
            }

            if (m_BroadcastDraftLegacyLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftLegacyAnnouncements)
                && draftLegacyAnnouncements != null
                && draftLegacyAnnouncements.Count > 0)
            {
                m_BroadcastLegacyLinePlatformAnnouncements[lineId] = CloneBroadcastLinePlatformAnnouncements(draftLegacyAnnouncements);
            }
            else
            {
                m_BroadcastLegacyLinePlatformAnnouncements.Remove(lineId);
            }
        }

        private bool IsBroadcastLineDraftDirty(string lineId)
        {
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> appliedBindings = GetBroadcastAppliedLineStationBindings(lineId);
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftBindings = GetBroadcastDraftLineStationBindingsOrNull(lineId);
            if (!AreBroadcastLineBindingsEqual(appliedBindings, draftBindings))
            {
                return true;
            }

            m_BroadcastLegacyLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> appliedLegacyBindings);
            m_BroadcastDraftLegacyLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftLegacyBindings);
            if (!AreBroadcastLineBindingsEqual(appliedLegacyBindings, draftLegacyBindings))
            {
                return true;
            }

            List<BroadcastWorkbenchRuleDto> appliedRules = GetBroadcastAppliedLineRules(lineId);
            List<BroadcastWorkbenchRuleDto> draftRules = GetBroadcastDraftLineRulesOrNull(lineId);
            if (!AreBroadcastRulesEqual(appliedRules, draftRules))
            {
                return true;
            }

            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> appliedAnnouncements =
                GetBroadcastAppliedLinePlatformAnnouncements(lineId);
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftAnnouncements =
                GetBroadcastDraftLinePlatformAnnouncementsOrNull(lineId);
            if (!AreBroadcastPlatformAnnouncementsEqual(appliedAnnouncements, draftAnnouncements))
            {
                return true;
            }

            m_BroadcastLegacyLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> appliedLegacyAnnouncements);
            m_BroadcastDraftLegacyLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftLegacyAnnouncements);
            return !AreBroadcastPlatformAnnouncementsEqual(appliedLegacyAnnouncements, draftLegacyAnnouncements);
        }

        private string[] BuildBroadcastWarnings(string lineId)
        {
            List<string> warnings = new List<string>();
            bool hasLegacyBindings =
                (m_BroadcastDraftLegacyLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> draftLegacyBindings)
                    && draftLegacyBindings != null
                    && draftLegacyBindings.Count > 0)
                || (m_BroadcastLegacyLineStationAssetBindings.TryGetValue(lineId, out Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> appliedLegacyBindings)
                    && appliedLegacyBindings != null
                    && appliedLegacyBindings.Count > 0);
            if (hasLegacyBindings)
            {
                warnings.Add("Some legacy station bindings could not be matched to current stations. They were kept out of active runtime.");
            }

            bool hasLegacyAnnouncements =
                (m_BroadcastDraftLegacyLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> draftLegacyAnnouncements)
                    && draftLegacyAnnouncements != null
                    && draftLegacyAnnouncements.Count > 0)
                || (m_BroadcastLegacyLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> appliedLegacyAnnouncements)
                    && appliedLegacyAnnouncements != null
                    && appliedLegacyAnnouncements.Count > 0);
            if (hasLegacyAnnouncements)
            {
                warnings.Add("Some legacy platform announcements could not be matched to current stations. They were kept out of active runtime.");
            }

            return warnings.ToArray();
        }

        private bool RemoveBroadcastAsset(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            bool removed = false;
            string normalizedAssetName = assetName.Trim();

            if (string.Equals(m_BroadcastPreviewAssetName, normalizedAssetName, StringComparison.OrdinalIgnoreCase))
            {
                MainThreadDispatcher.RunOnMainThread(() =>
                    StopBroadcastAssetPreviewOnMainThread(normalizedAssetName, notify: true));
            }

            for (int i = m_BroadcastAssetCatalog.Count - 1; i >= 0; i--)
            {
                BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog[i];
                if (!string.Equals(asset?.name, normalizedAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string assetPath = NormalizeBroadcastAssetFilePath(asset?.path);
                if (!string.IsNullOrEmpty(assetPath) && File.Exists(assetPath))
                {
                    File.Delete(assetPath);
                }

                m_BroadcastAssetCatalog.RemoveAt(i);
                removed = true;
            }

            if (!removed)
            {
                return false;
            }

            RemoveBroadcastAssetReferences(normalizedAssetName);
            MainThreadDispatcher.RunOnMainThread(() => RemoveBroadcastRuntimeAsset(normalizedAssetName));
            return true;
        }

        private void RemoveAllBroadcastAssets()
        {
            if (!string.IsNullOrEmpty(m_BroadcastPreviewAssetName))
            {
                string previewAssetName = m_BroadcastPreviewAssetName;
                MainThreadDispatcher.RunOnMainThread(() =>
                    StopBroadcastAssetPreviewOnMainThread(previewAssetName, notify: true));
            }

            for (int i = 0; i < m_BroadcastAssetCatalog.Count; i++)
            {
                string assetPath = NormalizeBroadcastAssetFilePath(m_BroadcastAssetCatalog[i]?.path);
                if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                {
                    continue;
                }

                File.Delete(assetPath);
            }

            m_BroadcastAssetCatalog.Clear();
            m_BroadcastDraftLineStationAssetBindings.Clear();
            m_BroadcastDraftLegacyLineStationAssetBindings.Clear();
            m_BroadcastLineStationAssetBindings.Clear();
            m_BroadcastLegacyLineStationAssetBindings.Clear();
            RemoveAllBroadcastAssetNodesFromRules();
            MainThreadDispatcher.RunOnMainThread(RemoveAllBroadcastRuntimeAssets);
        }

        private void RemoveBroadcastAssetReferences(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return;
            }

            RemoveBroadcastAssetReferencesFromBindings(m_BroadcastDraftLineStationAssetBindings, assetName);
            RemoveBroadcastAssetReferencesFromBindings(m_BroadcastDraftLegacyLineStationAssetBindings, assetName);
            RemoveBroadcastAssetReferencesFromBindings(m_BroadcastLineStationAssetBindings, assetName);
            RemoveBroadcastAssetReferencesFromBindings(m_BroadcastLegacyLineStationAssetBindings, assetName);
            RemoveBroadcastAssetReferencesFromRules(m_BroadcastDraftLineRules, assetName);
            RemoveBroadcastAssetReferencesFromRules(m_BroadcastLineRules, assetName);
            RemoveBroadcastAssetReferencesFromPlatformAnnouncements(m_BroadcastDraftLinePlatformAnnouncements, assetName);
            RemoveBroadcastAssetReferencesFromPlatformAnnouncements(m_BroadcastDraftLegacyLinePlatformAnnouncements, assetName);
            RemoveBroadcastAssetReferencesFromPlatformAnnouncements(m_BroadcastLinePlatformAnnouncements, assetName);
            RemoveBroadcastAssetReferencesFromPlatformAnnouncements(m_BroadcastLegacyLinePlatformAnnouncements, assetName);
        }

        private void RemoveAllBroadcastAssetNodesFromRules()
        {
            RemoveAllBroadcastAssetNodesFromRuleSet(m_BroadcastDraftLineRules);
            RemoveAllBroadcastAssetNodesFromRuleSet(m_BroadcastLineRules);
            RemoveAllBroadcastAssetNodesFromPlatformAnnouncements(m_BroadcastDraftLinePlatformAnnouncements);
            RemoveAllBroadcastAssetNodesFromPlatformAnnouncements(m_BroadcastDraftLegacyLinePlatformAnnouncements);
            RemoveAllBroadcastAssetNodesFromPlatformAnnouncements(m_BroadcastLinePlatformAnnouncements);
            RemoveAllBroadcastAssetNodesFromPlatformAnnouncements(m_BroadcastLegacyLinePlatformAnnouncements);
        }

        private Dictionary<string, string> BuildBroadcastAssetMatchLookup()
        {
            Dictionary<string, string> assetByStationKey = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> ambiguousKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < m_BroadcastAssetCatalog.Count; i++)
            {
                BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog[i];
                string assetName = asset?.name ?? string.Empty;
                string stationKey = NormalizeBroadcastStationMatchKey(assetName);
                if (string.IsNullOrEmpty(stationKey) || ambiguousKeys.Contains(stationKey))
                {
                    continue;
                }

                if (assetByStationKey.ContainsKey(stationKey))
                {
                    assetByStationKey.Remove(stationKey);
                    ambiguousKeys.Add(stationKey);
                    continue;
                }

                assetByStationKey[stationKey] = assetName;
            }

            return assetByStationKey;
        }

        private Dictionary<string, List<DispatchWorkbenchStationConflictDto>> BuildBroadcastAssetConflictLookup()
        {
            Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictsByStationKey =
                new Dictionary<string, List<DispatchWorkbenchStationConflictDto>>(StringComparer.Ordinal);
            for (int i = 0; i < m_BroadcastAssetCatalog.Count; i++)
            {
                BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog[i];
                string assetName = asset?.name ?? string.Empty;
                string stationKey = NormalizeBroadcastStationMatchKey(assetName);
                if (string.IsNullOrEmpty(stationKey))
                {
                    continue;
                }

                if (!conflictsByStationKey.TryGetValue(stationKey, out List<DispatchWorkbenchStationConflictDto> conflicts))
                {
                    conflicts = new List<DispatchWorkbenchStationConflictDto>();
                    conflictsByStationKey[stationKey] = conflicts;
                }

                conflicts.Add(new DispatchWorkbenchStationConflictDto
                {
                    assetName = assetName,
                    suggestedLang = string.Empty
                });
            }

            List<string> nonConflictKeys = null;
            foreach (KeyValuePair<string, List<DispatchWorkbenchStationConflictDto>> entry in conflictsByStationKey)
            {
                if (entry.Value == null || entry.Value.Count <= 1)
                {
                    nonConflictKeys ??= new List<string>();
                    nonConflictKeys.Add(entry.Key);
                    continue;
                }

                entry.Value.Sort((left, right) => string.Compare(left?.assetName, right?.assetName, StringComparison.OrdinalIgnoreCase));
            }

            if (nonConflictKeys != null)
            {
                for (int i = 0; i < nonConflictKeys.Count; i++)
                {
                    conflictsByStationKey.Remove(nonConflictKeys[i]);
                }
            }

            return conflictsByStationKey;
        }

        private void ApplyBroadcastStationConflicts(
            List<BroadcastWorkbenchStationGroup> stationGroups,
            Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup,
            string lineId)
        {
            if (stationGroups == null || stationGroups.Count == 0)
            {
                return;
            }

            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastDraftLineStationBindingsOrNull(lineId);
            for (int i = 0; i < stationGroups.Count; i++)
            {
                BroadcastWorkbenchStationGroup group = stationGroups[i];
                if (group?.Representative == null)
                {
                    continue;
                }

                List<DispatchWorkbenchStationConflictDto> conflicts =
                    BuildBroadcastStationConflictCandidates(NormalizeBroadcastStationMatchKey(group.Representative.name), conflictLookup);
                if (string.IsNullOrEmpty(group.Key)
                    || conflicts == null
                    || conflicts.Count <= 1)
                {
                    group.Representative.conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>();
                    continue;
                }

                HashSet<string> boundAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (lineBindings != null)
                {
                    if (lineBindings.TryGetValue(group.Representative.id ?? string.Empty, out List<BroadcastWorkbenchStationBindingDto> bindings)
                        && bindings != null)
                    {
                        for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                        {
                            string boundAssetName = bindings[bindingIndex]?.assetName ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(boundAssetName))
                            {
                                boundAssetNames.Add(boundAssetName);
                            }
                        }
                    }
                }

                DispatchWorkbenchStationConflictDto[] remainingConflicts = conflicts
                    .Select(conflict => conflict == null
                        ? null
                        : new DispatchWorkbenchStationConflictDto
                        {
                            assetName = conflict.assetName ?? string.Empty,
                            suggestedLang = conflict.suggestedLang ?? string.Empty
                        })
                    .Where(conflict => conflict != null
                        && !string.IsNullOrWhiteSpace(conflict.assetName)
                        && !boundAssetNames.Contains(conflict.assetName))
                    .ToArray();
                group.Representative.conflictAssets = remainingConflicts;
            }
        }

        private void ApplyBroadcastPendingAutoBindConflicts(
            List<BroadcastWorkbenchStationGroup> stationGroups,
            string lineId)
        {
            if (stationGroups == null || stationGroups.Count == 0)
            {
                return;
            }

            Dictionary<string, DispatchWorkbenchStationConflictDto[]> pendingConflictsByStationKey = null;
            if (!string.IsNullOrEmpty(lineId))
            {
                m_BroadcastPendingAutoBindConflicts.TryGetValue(lineId, out pendingConflictsByStationKey);
            }

            for (int i = 0; i < stationGroups.Count; i++)
            {
                BroadcastWorkbenchStationGroup group = stationGroups[i];
                if (group?.Representative == null)
                {
                    continue;
                }

                if (pendingConflictsByStationKey != null
                    && !string.IsNullOrEmpty(group.Key)
                    && pendingConflictsByStationKey.TryGetValue(group.Key, out DispatchWorkbenchStationConflictDto[] pendingConflicts)
                    && pendingConflicts != null
                    && pendingConflicts.Length > 0)
                {
                    group.Representative.conflictAssets = pendingConflicts
                        .Select(conflict => conflict == null
                            ? null
                            : new DispatchWorkbenchStationConflictDto
                            {
                                assetName = conflict.assetName ?? string.Empty,
                                suggestedLang = conflict.suggestedLang ?? string.Empty
                            })
                        .Where(conflict => conflict != null && !string.IsNullOrWhiteSpace(conflict.assetName))
                        .ToArray();
                    continue;
                }

                group.Representative.conflictAssets = Array.Empty<DispatchWorkbenchStationConflictDto>();
            }
        }

        private void StoreBroadcastPendingAutoBindConflicts(
            string lineId,
            List<BroadcastWorkbenchStationGroup> stationGroups)
        {
            if (string.IsNullOrEmpty(lineId))
            {
                return;
            }

            Dictionary<string, DispatchWorkbenchStationConflictDto[]> conflictsByStationKey =
                new Dictionary<string, DispatchWorkbenchStationConflictDto[]>(StringComparer.Ordinal);
            if (stationGroups != null)
            {
                for (int i = 0; i < stationGroups.Count; i++)
                {
                    BroadcastWorkbenchStationGroup group = stationGroups[i];
                    if (group?.Representative == null
                        || string.IsNullOrEmpty(group.Key)
                        || group.Representative.conflictAssets == null
                        || group.Representative.conflictAssets.Length == 0)
                    {
                        continue;
                    }

                    DispatchWorkbenchStationConflictDto[] clonedConflicts = group.Representative.conflictAssets
                        .Select(conflict => conflict == null
                            ? null
                            : new DispatchWorkbenchStationConflictDto
                            {
                                assetName = conflict.assetName ?? string.Empty,
                                suggestedLang = conflict.suggestedLang ?? string.Empty
                            })
                        .Where(conflict => conflict != null && !string.IsNullOrWhiteSpace(conflict.assetName))
                        .ToArray();
                    if (clonedConflicts.Length > 0)
                    {
                        conflictsByStationKey[group.Key] = clonedConflicts;
                    }
                }
            }

            if (conflictsByStationKey.Count == 0)
            {
                m_BroadcastPendingAutoBindConflicts.Remove(lineId);
                return;
            }

            m_BroadcastPendingAutoBindConflicts[lineId] = conflictsByStationKey;
        }

        private void ClearBroadcastPendingAutoBindConflicts(string lineId)
        {
            if (!string.IsNullOrEmpty(lineId))
            {
                m_BroadcastPendingAutoBindConflicts.Remove(lineId);
            }
        }

        private void ClearBroadcastPendingAutoBindConflict(string lineId, string stationKey)
        {
            if (string.IsNullOrEmpty(lineId)
                || string.IsNullOrEmpty(stationKey)
                || !m_BroadcastPendingAutoBindConflicts.TryGetValue(lineId, out Dictionary<string, DispatchWorkbenchStationConflictDto[]> lineConflicts))
            {
                return;
            }

            lineConflicts.Remove(stationKey);
            if (lineConflicts.Count == 0)
            {
                m_BroadcastPendingAutoBindConflicts.Remove(lineId);
            }
        }

        private List<DispatchWorkbenchStationConflictDto> BuildBroadcastStationConflictCandidates(
            string stationKey,
            Dictionary<string, List<DispatchWorkbenchStationConflictDto>> conflictLookup)
        {
            if (string.IsNullOrEmpty(stationKey))
            {
                return null;
            }

            Dictionary<string, DispatchWorkbenchStationConflictDto> candidatesByAssetName =
                new Dictionary<string, DispatchWorkbenchStationConflictDto>(StringComparer.OrdinalIgnoreCase);

            if (conflictLookup != null
                && conflictLookup.TryGetValue(stationKey, out List<DispatchWorkbenchStationConflictDto> exactConflicts)
                && exactConflicts != null)
            {
                for (int i = 0; i < exactConflicts.Count; i++)
                {
                    DispatchWorkbenchStationConflictDto conflict = exactConflicts[i];
                    string assetName = conflict?.assetName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        continue;
                    }

                    candidatesByAssetName[assetName] = new DispatchWorkbenchStationConflictDto
                    {
                        assetName = assetName,
                        suggestedLang = conflict?.suggestedLang ?? string.Empty
                    };
                }
            }

            for (int i = 0; i < m_BroadcastAssetCatalog.Count; i++)
            {
                string assetName = m_BroadcastAssetCatalog[i]?.name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(assetName)
                    || !DoesBroadcastAssetMatchStationKey(assetName, stationKey))
                {
                    continue;
                }

                if (!candidatesByAssetName.ContainsKey(assetName))
                {
                    candidatesByAssetName[assetName] = new DispatchWorkbenchStationConflictDto
                    {
                        assetName = assetName,
                        suggestedLang = string.Empty
                    };
                }
            }

            if (candidatesByAssetName.Count <= 1)
            {
                return candidatesByAssetName.Values.ToList();
            }

            return candidatesByAssetName.Values
                .OrderBy(conflict => conflict.assetName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool DoesBroadcastAssetMatchStationKey(string assetName, string stationKey)
        {
            string normalizedStationKey = stationKey?.Trim() ?? string.Empty;
            string normalizedAssetKey = NormalizeBroadcastStationMatchKey(assetName);
            if (string.IsNullOrEmpty(normalizedStationKey) || string.IsNullOrEmpty(normalizedAssetKey))
            {
                return false;
            }

            if (normalizedAssetKey.IndexOf(normalizedStationKey, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            string compactStationKey = CompactBroadcastStationMatchKey(normalizedStationKey);
            string compactAssetKey = CompactBroadcastStationMatchKey(normalizedAssetKey);
            if (string.IsNullOrEmpty(compactStationKey) || string.IsNullOrEmpty(compactAssetKey))
            {
                return false;
            }

            return compactAssetKey.IndexOf(compactStationKey, StringComparison.Ordinal) >= 0;
        }

        private static void RemoveBroadcastAssetReferencesFromBindings(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> allBindings,
            string assetName)
        {
            List<string> emptyBindingLineIds = null;
            foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> lineEntry in allBindings)
            {
                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> bindings = lineEntry.Value;
                if (bindings == null || bindings.Count == 0)
                {
                    continue;
                }

                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> updatedBindings = null;
                List<string> emptyStationIds = null;
                foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> binding in bindings)
                {
                    List<BroadcastWorkbenchStationBindingDto> stationBindings = binding.Value?
                        .Where(entry => entry != null
                            && !string.Equals(entry.assetName, assetName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (stationBindings == null || stationBindings.Count == 0)
                    {
                        emptyStationIds ??= new List<string>();
                        emptyStationIds.Add(binding.Key);
                        continue;
                    }

                    updatedBindings ??= new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                    updatedBindings[binding.Key] = NormalizeBroadcastStationBindings(binding.Key, stationBindings);
                }

                if (updatedBindings != null)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> updatedBinding in updatedBindings)
                    {
                        bindings[updatedBinding.Key] = updatedBinding.Value;
                    }
                }

                if (emptyStationIds != null)
                {
                    for (int i = 0; i < emptyStationIds.Count; i++)
                    {
                        bindings.Remove(emptyStationIds[i]);
                    }
                }

                if (bindings.Count == 0)
                {
                    emptyBindingLineIds ??= new List<string>();
                    emptyBindingLineIds.Add(lineEntry.Key);
                }
            }

            if (emptyBindingLineIds == null)
            {
                return;
            }

            for (int i = 0; i < emptyBindingLineIds.Count; i++)
            {
                allBindings.Remove(emptyBindingLineIds[i]);
            }
        }

        private static void RemoveBroadcastAssetReferencesFromRules(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> allRules,
            string assetName)
        {
            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> lineEntry in allRules)
            {
                List<BroadcastWorkbenchRuleDto> rules = lineEntry.Value;
                if (rules == null)
                {
                    continue;
                }

                for (int i = 0; i < rules.Count; i++)
                {
                    BroadcastWorkbenchRuleDto rule = rules[i];
                    if (rule?.nodes == null || rule.nodes.Length == 0)
                    {
                        continue;
                    }

                    rule.nodes = rule.nodes
                        .Where(node => node != null
                            && !(string.Equals(node.type, "asset", StringComparison.Ordinal)
                                && string.Equals(node.name, assetName, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                }
            }
        }

        private static void RemoveAllBroadcastAssetNodesFromRuleSet(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> allRules)
        {
            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> lineEntry in allRules)
            {
                List<BroadcastWorkbenchRuleDto> rules = lineEntry.Value;
                if (rules == null)
                {
                    continue;
                }

                for (int i = 0; i < rules.Count; i++)
                {
                    BroadcastWorkbenchRuleDto rule = rules[i];
                    if (rule?.nodes == null || rule.nodes.Length == 0)
                    {
                        continue;
                    }

                    rule.nodes = rule.nodes
                        .Where(node => node != null && !string.Equals(node.type, "asset", StringComparison.Ordinal))
                        .ToArray();
                }
            }
        }

        private static void RemoveBroadcastAssetReferencesFromPlatformAnnouncements(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> allAnnouncements,
            string assetName)
        {
            foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> lineEntry in allAnnouncements)
            {
                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements = lineEntry.Value;
                if (announcements == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> stationEntry in announcements)
                {
                    BroadcastWorkbenchPlatformAnnouncementDto announcement = stationEntry.Value;
                    if (announcement?.nodes == null || announcement.nodes.Length == 0)
                    {
                        continue;
                    }

                    announcement.nodes = announcement.nodes
                        .Where(node => node != null
                            && !(string.Equals(node.type, "asset", StringComparison.Ordinal)
                                && string.Equals(node.name, assetName, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                }
            }
        }

        private static void RemoveAllBroadcastAssetNodesFromPlatformAnnouncements(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> allAnnouncements)
        {
            foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> lineEntry in allAnnouncements)
            {
                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements = lineEntry.Value;
                if (announcements == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> stationEntry in announcements)
                {
                    BroadcastWorkbenchPlatformAnnouncementDto announcement = stationEntry.Value;
                    if (announcement?.nodes == null || announcement.nodes.Length == 0)
                    {
                        continue;
                    }

                    announcement.nodes = announcement.nodes
                        .Where(node => node != null && !string.Equals(node.type, "asset", StringComparison.Ordinal))
                        .ToArray();
                }
            }
        }

        private static void RestoreBroadcastLineBindingsInto(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> target,
            BroadcastWorkbenchPersistedLineBindingState[] persistedLineBindings)
        {
            if (persistedLineBindings == null)
            {
                return;
            }

            for (int i = 0; i < persistedLineBindings.Length; i++)
            {
                BroadcastWorkbenchPersistedLineBindingState lineBinding = persistedLineBindings[i];
                if (lineBinding == null || string.IsNullOrWhiteSpace(lineBinding.lineId) || lineBinding.stationBindings == null)
                {
                    continue;
                }

                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                    new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                for (int j = 0; j < lineBinding.stationBindings.Length; j++)
                {
                    BroadcastWorkbenchStationBindingDto stationBinding = lineBinding.stationBindings[j];
                    if (stationBinding == null
                        || string.IsNullOrWhiteSpace(stationBinding.stationId)
                        || string.IsNullOrWhiteSpace(stationBinding.assetName))
                    {
                        continue;
                    }

                    if (!lineBindings.TryGetValue(stationBinding.stationId, out List<BroadcastWorkbenchStationBindingDto> stationBindings))
                    {
                        stationBindings = new List<BroadcastWorkbenchStationBindingDto>();
                        lineBindings[stationBinding.stationId] = stationBindings;
                    }

                    stationBindings.Add(stationBinding);
                }

                Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> normalizedByStationId = null;
                List<string> emptyStationIds = null;
                foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> stationEntry in lineBindings)
                {
                    List<BroadcastWorkbenchStationBindingDto> normalizedBindings =
                        NormalizeBroadcastStationBindings(stationEntry.Key, stationEntry.Value);
                    if (normalizedBindings.Count == 0)
                    {
                        emptyStationIds ??= new List<string>();
                        emptyStationIds.Add(stationEntry.Key);
                        continue;
                    }

                    normalizedByStationId ??= new Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>(StringComparer.Ordinal);
                    normalizedByStationId[stationEntry.Key] = normalizedBindings;
                }

                if (normalizedByStationId != null)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> normalizedEntry in normalizedByStationId)
                    {
                        lineBindings[normalizedEntry.Key] = normalizedEntry.Value;
                    }
                }

                if (emptyStationIds != null)
                {
                    for (int stationIndex = 0; stationIndex < emptyStationIds.Count; stationIndex++)
                    {
                        lineBindings.Remove(emptyStationIds[stationIndex]);
                    }
                }

                if (lineBindings.Count > 0)
                {
                    target[lineBinding.lineId] = lineBindings;
                }
            }
        }

        private static void RestoreBroadcastRulesInto(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> target,
            BroadcastWorkbenchPersistedRuleState[] persistedRules)
        {
            if (persistedRules == null)
            {
                return;
            }

            for (int i = 0; i < persistedRules.Length; i++)
            {
                BroadcastWorkbenchPersistedRuleState ruleState = persistedRules[i];
                if (ruleState == null || string.IsNullOrWhiteSpace(ruleState.lineId))
                {
                    continue;
                }

                List<BroadcastWorkbenchRuleDto> normalizedRules = NormalizeBroadcastRules(ruleState.rules);
                if (normalizedRules.Count > 0)
                {
                    target[ruleState.lineId] = normalizedRules;
                }
            }
        }

        private static void RestoreBroadcastPlatformAnnouncementsInto(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> target,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedAnnouncements)
        {
            if (persistedAnnouncements == null)
            {
                return;
            }

            for (int i = 0; i < persistedAnnouncements.Length; i++)
            {
                BroadcastWorkbenchPersistedPlatformAnnouncementState lineState = persistedAnnouncements[i];
                if (lineState == null || string.IsNullOrWhiteSpace(lineState.lineId) || lineState.announcements == null)
                {
                    continue;
                }

                Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements =
                    new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
                for (int j = 0; j < lineState.announcements.Length; j++)
                {
                    BroadcastWorkbenchPlatformAnnouncementDto announcement = lineState.announcements[j];
                    if (announcement == null || string.IsNullOrWhiteSpace(announcement.stationId))
                    {
                        continue;
                    }

                    BroadcastWorkbenchPlatformAnnouncementDto normalizedAnnouncement = NormalizeBroadcastPlatformAnnouncement(
                        lineState.lineId,
                        announcement.stationId,
                        announcement.stationName,
                        announcement.title,
                        announcement.uiTriggerId,
                        announcement.enabled,
                        announcement.nodes);
                    lineAnnouncements[BuildBroadcastPlatformAnnouncementStorageKey(
                        normalizedAnnouncement.stationId,
                        normalizedAnnouncement.uiTriggerId)] = normalizedAnnouncement;
                }

                if (lineAnnouncements.Count > 0)
                {
                    target[lineState.lineId] = lineAnnouncements;
                }
            }
        }

        private static void CopyBroadcastBindings(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source,
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> target)
        {
            foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> entry in source)
            {
                if (entry.Value == null || entry.Value.Count == 0)
                {
                    continue;
                }

                target[entry.Key] = CloneBroadcastLineBindings(entry.Value);
            }
        }

        private static void CopyBroadcastRules(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source,
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> target)
        {
            foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> entry in source)
            {
                if (entry.Value == null || entry.Value.Count == 0)
                {
                    continue;
                }

                target[entry.Key] = entry.Value
                    .Select(CloneBroadcastWorkbenchRule)
                    .Where(rule => rule != null)
                    .ToList();
            }
        }

        private static void CopyBroadcastPlatformAnnouncements(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source,
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> target)
        {
            foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> entry in source)
            {
                if (entry.Value == null || entry.Value.Count == 0)
                {
                    continue;
                }

                target[entry.Key] = CloneBroadcastLinePlatformAnnouncements(entry.Value);
            }
        }

        private static bool AreBroadcastLineBindingsEqual(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> left,
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> right)
        {
            string leftJson = DispatchWorkbenchJson.Serialize(FlattenBroadcastLineBindings(left));
            string rightJson = DispatchWorkbenchJson.Serialize(FlattenBroadcastLineBindings(right));
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        private static bool AreBroadcastRulesEqual(
            List<BroadcastWorkbenchRuleDto> left,
            List<BroadcastWorkbenchRuleDto> right)
        {
            string leftJson = DispatchWorkbenchJson.Serialize(
                left?.Select(CloneBroadcastWorkbenchRule).Where(rule => rule != null).ToArray()
                ?? Array.Empty<BroadcastWorkbenchRuleDto>());
            string rightJson = DispatchWorkbenchJson.Serialize(
                right?.Select(CloneBroadcastWorkbenchRule).Where(rule => rule != null).ToArray()
                ?? Array.Empty<BroadcastWorkbenchRuleDto>());
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        private static bool AreBroadcastPlatformAnnouncementsEqual(
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> left,
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> right)
        {
            string leftJson = DispatchWorkbenchJson.Serialize(FlattenBroadcastLinePlatformAnnouncements(left));
            string rightJson = DispatchWorkbenchJson.Serialize(FlattenBroadcastLinePlatformAnnouncements(right));
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        private static string NormalizeBroadcastStationMatchKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string source = Path.GetFileNameWithoutExtension(value.Trim());
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(source.Length);
            bool lastWasSeparator = false;
            for (int i = 0; i < source.Length; i++)
            {
                char ch = char.ToLowerInvariant(source[i]);
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                    lastWasSeparator = false;
                    continue;
                }

                if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-')
                {
                    if (!lastWasSeparator && builder.Length > 0)
                    {
                        builder.Append(' ');
                        lastWasSeparator = true;
                    }
                }
            }

            return builder.ToString().Trim();
        }

        private static string CompactBroadcastStationMatchKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (!char.IsWhiteSpace(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private BroadcastWorkbenchPersistedAssetState[] BuildPersistedBroadcastAssetStates()
        {
            return m_BroadcastAssetCatalog
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

        private BroadcastWorkbenchPersistedLineBindingState[] BuildPersistedBroadcastDraftLineBindingStates()
        {
            return BuildPersistedBroadcastLineBindingStates(m_BroadcastDraftLineStationAssetBindings);
        }

        private BroadcastWorkbenchPersistedLineBindingState[] BuildPersistedBroadcastDraftLegacyLineBindingStates()
        {
            return BuildPersistedBroadcastLineBindingStates(m_BroadcastDraftLegacyLineStationAssetBindings);
        }

        private BroadcastWorkbenchPersistedLineBindingState[] BuildPersistedBroadcastLineBindingStates()
        {
            return BuildPersistedBroadcastLineBindingStates(m_BroadcastLineStationAssetBindings);
        }

        private BroadcastWorkbenchPersistedLineBindingState[] BuildPersistedBroadcastLegacyLineBindingStates()
        {
            return BuildPersistedBroadcastLineBindingStates(m_BroadcastLegacyLineStationAssetBindings);
        }

        private static BroadcastWorkbenchPersistedLineBindingState[] BuildPersistedBroadcastLineBindingStates(
            Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedLineBindingState
                {
                    lineId = entry.Key ?? string.Empty,
                    stationBindings = entry.Value == null
                        ? Array.Empty<BroadcastWorkbenchStationBindingDto>()
                        : FlattenBroadcastLineBindings(entry.Value)
                })
                .Where(entry => entry.stationBindings.Length > 0)
                .ToArray();
        }

        private BroadcastWorkbenchPersistedRuleState[] BuildPersistedBroadcastDraftRuleStates()
        {
            return BuildPersistedBroadcastRuleStates(m_BroadcastDraftLineRules);
        }

        private BroadcastWorkbenchPersistedRuleState[] BuildPersistedBroadcastRuleStates()
        {
            return BuildPersistedBroadcastRuleStates(m_BroadcastLineRules);
        }

        private static BroadcastWorkbenchPersistedRuleState[] BuildPersistedBroadcastRuleStates(
            Dictionary<string, List<BroadcastWorkbenchRuleDto>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedRuleState
                {
                    lineId = entry.Key ?? string.Empty,
                    rules = entry.Value == null
                        ? Array.Empty<BroadcastWorkbenchRuleDto>()
                        : entry.Value
                            .Select(CloneBroadcastWorkbenchRule)
                            .Where(rule => rule != null)
                            .ToArray()
                })
                .Where(entry => entry.rules.Length > 0)
                .ToArray();
        }

        private BroadcastWorkbenchPersistedPlatformAnnouncementState[] BuildPersistedBroadcastDraftPlatformAnnouncementStates()
        {
            return BuildPersistedBroadcastPlatformAnnouncementStates(m_BroadcastDraftLinePlatformAnnouncements);
        }

        private BroadcastWorkbenchPersistedPlatformAnnouncementState[] BuildPersistedBroadcastDraftLegacyPlatformAnnouncementStates()
        {
            return BuildPersistedBroadcastPlatformAnnouncementStates(m_BroadcastDraftLegacyLinePlatformAnnouncements);
        }

        private BroadcastWorkbenchPersistedPlatformAnnouncementState[] BuildPersistedBroadcastPlatformAnnouncementStates()
        {
            return BuildPersistedBroadcastPlatformAnnouncementStates(m_BroadcastLinePlatformAnnouncements);
        }

        private BroadcastWorkbenchPersistedPlatformAnnouncementState[] BuildPersistedBroadcastLegacyPlatformAnnouncementStates()
        {
            return BuildPersistedBroadcastPlatformAnnouncementStates(m_BroadcastLegacyLinePlatformAnnouncements);
        }

        private static BroadcastWorkbenchPersistedPlatformAnnouncementState[] BuildPersistedBroadcastPlatformAnnouncementStates(
            Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> source)
        {
            return source
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BroadcastWorkbenchPersistedPlatformAnnouncementState
                {
                    lineId = entry.Key ?? string.Empty,
                    announcements = FlattenBroadcastLinePlatformAnnouncements(entry.Value)
                })
                .Where(entry => entry.announcements.Length > 0)
                .ToArray();
        }

        private static BroadcastWorkbenchPlatformAnnouncementDto[] FlattenBroadcastLinePlatformAnnouncements(
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
        {
            if (lineAnnouncements == null || lineAnnouncements.Count == 0)
            {
                return Array.Empty<BroadcastWorkbenchPlatformAnnouncementDto>();
            }

            return lineAnnouncements
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => CloneBroadcastPlatformAnnouncement(
                    entry.Value,
                    entry.Value?.lineId ?? string.Empty,
                    entry.Value?.stationId ?? string.Empty,
                    entry.Value?.stationName ?? string.Empty))
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.stationId))
                .ToArray();
        }

        private BroadcastWorkbenchPersistedAppliedState BuildPersistedBroadcastAppliedState()
        {
            return new BroadcastWorkbenchPersistedAppliedState
            {
                lineIds = m_BroadcastAppliedLineIds.OrderBy(lineId => lineId, StringComparer.Ordinal).ToArray(),
                volume = ClampBroadcastVolumePercent(m_BroadcastAppliedVolumePercent)
            };
        }

        private void RestoreBroadcastWorkbenchPersistence(
            string broadcastAssetDirectory,
            BroadcastWorkbenchPersistedAssetState[] persistedAssets,
            BroadcastWorkbenchPersistedLineBindingState[] persistedDraftLineBindings,
            BroadcastWorkbenchPersistedLineBindingState[] persistedDraftLegacyLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedDraftRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedDraftPlatformAnnouncements,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedDraftLegacyPlatformAnnouncements,
            BroadcastWorkbenchPersistedLineBindingState[] persistedLineBindings,
            BroadcastWorkbenchPersistedLineBindingState[] persistedLegacyLineBindings,
            BroadcastWorkbenchPersistedRuleState[] persistedRules,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedPlatformAnnouncements,
            BroadcastWorkbenchPersistedPlatformAnnouncementState[] persistedLegacyPlatformAnnouncements,
            BroadcastWorkbenchPersistedAppliedState persistedAppliedState,
            int persistedDraftVolume)
        {
            m_BroadcastAssetCatalog.Clear();
            m_BroadcastDraftLineStationAssetBindings.Clear();
            m_BroadcastDraftLegacyLineStationAssetBindings.Clear();
            m_BroadcastDraftLineRules.Clear();
            m_BroadcastDraftLinePlatformAnnouncements.Clear();
            m_BroadcastDraftLegacyLinePlatformAnnouncements.Clear();
            m_BroadcastLineStationAssetBindings.Clear();
            m_BroadcastLegacyLineStationAssetBindings.Clear();
            m_BroadcastLineRules.Clear();
            m_BroadcastLinePlatformAnnouncements.Clear();
            m_BroadcastLegacyLinePlatformAnnouncements.Clear();
            m_BroadcastAppliedLineIds.Clear();
            m_BroadcastRuntimeCheckedLineIds.Clear();
            m_BroadcastDraftVolumePercent = ClampBroadcastVolumePercent(persistedDraftVolume);
            m_BroadcastAppliedVolumePercent = ClampBroadcastVolumePercent(persistedAppliedState?.volume ?? m_BroadcastDraftVolumePercent);
            m_BroadcastExternalBrowseDirectory = string.Empty;
            m_BroadcastAssetDirectory = EnsureBroadcastManagedAssetDirectory();

            string managedAssetDirectory = NormalizeBroadcastAssetDirectory(m_BroadcastAssetDirectory);
            if (!string.IsNullOrEmpty(broadcastAssetDirectory))
            {
                string persistedDirectory = NormalizeBroadcastAssetDirectory(broadcastAssetDirectory);
                if (!string.IsNullOrEmpty(persistedDirectory))
                {
                    managedAssetDirectory = persistedDirectory;
                }
            }

            m_BroadcastAssetDirectory = managedAssetDirectory;

            if (persistedAssets != null)
            {
                for (int i = 0; i < persistedAssets.Length; i++)
                {
                    BroadcastWorkbenchPersistedAssetState asset = persistedAssets[i];
                    if (asset == null || string.IsNullOrWhiteSpace(asset.name) || string.IsNullOrEmpty(managedAssetDirectory))
                    {
                        continue;
                    }

                    string candidatePath = Path.Combine(managedAssetDirectory, asset.name);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    m_BroadcastAssetCatalog.Add(new BroadcastWorkbenchAssetDto
                    {
                        name = asset.name ?? string.Empty,
                        desc = !string.IsNullOrEmpty(asset.desc)
                            ? asset.desc
                            : (asset.extension ?? string.Empty).TrimStart('.').ToUpperInvariant(),
                        length = asset.length ?? string.Empty,
                        path = NormalizeBroadcastAssetFilePath(candidatePath),
                        extension = !string.IsNullOrEmpty(asset.extension)
                            ? asset.extension
                            : (Path.GetExtension(candidatePath) ?? string.Empty)
                    });
                }
            }

            RestoreBroadcastLineBindingsInto(
                m_BroadcastDraftLineStationAssetBindings,
                persistedDraftLineBindings);

            RestoreBroadcastLineBindingsInto(
                m_BroadcastDraftLegacyLineStationAssetBindings,
                persistedDraftLegacyLineBindings);

            RestoreBroadcastRulesInto(
                m_BroadcastDraftLineRules,
                persistedDraftRules);

            RestoreBroadcastPlatformAnnouncementsInto(
                m_BroadcastDraftLinePlatformAnnouncements,
                persistedDraftPlatformAnnouncements);

            RestoreBroadcastPlatformAnnouncementsInto(
                m_BroadcastDraftLegacyLinePlatformAnnouncements,
                persistedDraftLegacyPlatformAnnouncements);

            RestoreBroadcastLineBindingsInto(
                m_BroadcastLineStationAssetBindings,
                persistedLineBindings);

            RestoreBroadcastLineBindingsInto(
                m_BroadcastLegacyLineStationAssetBindings,
                persistedLegacyLineBindings);

            RestoreBroadcastRulesInto(
                m_BroadcastLineRules,
                persistedRules);

            RestoreBroadcastPlatformAnnouncementsInto(
                m_BroadcastLinePlatformAnnouncements,
                persistedPlatformAnnouncements);

            RestoreBroadcastPlatformAnnouncementsInto(
                m_BroadcastLegacyLinePlatformAnnouncements,
                persistedLegacyPlatformAnnouncements);

            if (persistedAppliedState?.lineIds != null)
            {
                for (int i = 0; i < persistedAppliedState.lineIds.Length; i++)
                {
                    string lineId = persistedAppliedState.lineIds[i];
                    if (string.IsNullOrWhiteSpace(lineId))
                    {
                        continue;
                    }

                    m_BroadcastAppliedLineIds.Add(lineId);
                }
            }
            if (m_BroadcastDraftLineStationAssetBindings.Count == 0 && persistedLineBindings != null)
            {
                CopyBroadcastBindings(m_BroadcastLineStationAssetBindings, m_BroadcastDraftLineStationAssetBindings);
            }

            if (m_BroadcastDraftLineRules.Count == 0 && persistedRules != null)
            {
                CopyBroadcastRules(m_BroadcastLineRules, m_BroadcastDraftLineRules);
            }

            if (m_BroadcastDraftLinePlatformAnnouncements.Count == 0 && persistedPlatformAnnouncements != null)
            {
                CopyBroadcastPlatformAnnouncements(m_BroadcastLinePlatformAnnouncements, m_BroadcastDraftLinePlatformAnnouncements);
            }

            ApplyBroadcastPreviewVolumeToActivePreviewSources();
            ApplyBroadcastAppliedVolumeToRuntime();
        }

        private static BroadcastWorkbenchAssetDto CloneBroadcastWorkbenchAsset(BroadcastWorkbenchAssetDto asset)
        {
            if (asset == null)
            {
                return new BroadcastWorkbenchAssetDto();
            }

            return new BroadcastWorkbenchAssetDto
            {
                name = asset.name ?? string.Empty,
                desc = asset.desc ?? string.Empty,
                length = asset.length ?? string.Empty,
                path = asset.path ?? string.Empty,
                extension = asset.extension ?? string.Empty
            };
        }

        private static BroadcastWorkbenchRuleDto CloneBroadcastWorkbenchRule(BroadcastWorkbenchRuleDto rule)
        {
            if (rule == null)
            {
                return null;
            }

            return new BroadcastWorkbenchRuleDto
            {
                id = rule.id ?? string.Empty,
                title = rule.title ?? string.Empty,
                titleKey = rule.titleKey ?? string.Empty,
                triggerId = rule.triggerId ?? string.Empty,
                trigger = rule.trigger ?? string.Empty,
                triggerKey = rule.triggerKey ?? string.Empty,
                nodes = rule.nodes == null
                    ? Array.Empty<BroadcastWorkbenchRuleNodeDto>()
                    : rule.nodes
                        .Select(CloneBroadcastWorkbenchRuleNode)
                        .Where(node => node != null)
                        .ToArray()
            };
        }

        private static BroadcastWorkbenchRuleNodeDto CloneBroadcastWorkbenchRuleNode(BroadcastWorkbenchRuleNodeDto node)
        {
            if (node == null)
            {
                return null;
            }

            return new BroadcastWorkbenchRuleNodeDto
            {
                id = node.id ?? string.Empty,
                type = node.type ?? string.Empty,
                name = node.name ?? string.Empty,
                nameKey = node.nameKey ?? string.Empty,
                desc = node.desc ?? string.Empty,
                descKey = node.descKey ?? string.Empty,
                langIndex = node.langIndex > 0 ? node.langIndex : 1,
                delaySeconds = node.delaySeconds < 0f ? 0f : node.delaySeconds
            };
        }

        private static BroadcastWorkbenchPlatformAnnouncementDto CloneBroadcastPlatformAnnouncement(
            BroadcastWorkbenchPlatformAnnouncementDto announcement,
            string lineId,
            string stationId,
            string stationName)
        {
            if (announcement == null)
            {
                return NormalizeBroadcastPlatformAnnouncement(lineId, stationId, stationName, string.Empty, "platform_idle_clear", false, null);
            }

            return NormalizeBroadcastPlatformAnnouncement(
                lineId,
                stationId,
                string.IsNullOrWhiteSpace(stationName) ? announcement.stationName : stationName,
                announcement.title,
                announcement.uiTriggerId,
                announcement.enabled,
                announcement.nodes);
        }

        private static BroadcastWorkbenchPlatformAnnouncementDto NormalizeBroadcastPlatformAnnouncement(
            string lineId,
            string stationId,
            string stationName,
            string title,
            string uiTriggerId,
            bool enabled,
            IEnumerable<BroadcastWorkbenchRuleNodeDto> nodes)
        {
            string normalizedUiTriggerId = NormalizeBroadcastPlatformUiTriggerId(uiTriggerId);
            return new BroadcastWorkbenchPlatformAnnouncementDto
            {
                lineId = lineId ?? string.Empty,
                stationId = stationId ?? string.Empty,
                stationName = stationName ?? string.Empty,
                title = title ?? string.Empty,
                uiTriggerId = normalizedUiTriggerId,
                enabled = enabled,
                triggerId = ResolveBroadcastPlatformRuntimeTriggerId(normalizedUiTriggerId),
                cooldownGameMinutes = 20,
                nodes = nodes == null
                    ? Array.Empty<BroadcastWorkbenchRuleNodeDto>()
                    : nodes
                        .Select(CloneBroadcastWorkbenchRuleNode)
                        .Where(node => node != null
                            && !string.IsNullOrWhiteSpace(node.id)
                            && !string.IsNullOrWhiteSpace(node.type))
                        .ToArray()
            };
        }

        private static string NormalizeBroadcastPlatformUiTriggerId(string uiTriggerId)
        {
            switch ((uiTriggerId ?? string.Empty).Trim())
            {
                case "approach_station":
                case "platform_approach_station":
                    return "approach_station";
                case "platform_idle_clear":
                default:
                    return "platform_idle_clear";
            }
        }

        private static string ResolveBroadcastPlatformRuntimeTriggerId(string uiTriggerId)
        {
            return string.Equals(uiTriggerId, "approach_station", StringComparison.Ordinal)
                ? "platform_approach_station"
                : "platform_idle_clear";
        }

        private static Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> CloneBroadcastLinePlatformAnnouncements(
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> source)
        {
            Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> clone =
                new Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>(StringComparer.Ordinal);
            if (source == null)
            {
                return clone;
            }

            foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in source)
            {
                BroadcastWorkbenchPlatformAnnouncementDto announcement = CloneBroadcastPlatformAnnouncement(
                    entry.Value,
                    entry.Value?.lineId ?? string.Empty,
                    entry.Value?.stationId ?? string.Empty,
                    entry.Value?.stationName ?? string.Empty);
                string storageKey = BuildBroadcastPlatformAnnouncementStorageKey(
                    announcement?.stationId,
                    announcement?.uiTriggerId);
                if (!string.IsNullOrWhiteSpace(storageKey))
                {
                    clone[storageKey] = announcement;
                }
            }

            return clone;
        }

        private static string BuildBroadcastPlatformAnnouncementStorageKey(string stationId, string uiTriggerId)
        {
            string normalizedStationId = stationId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedStationId))
            {
                return string.Empty;
            }

            string normalizedUiTriggerId = NormalizeBroadcastPlatformUiTriggerId(uiTriggerId);
            return normalizedStationId + "|" + normalizedUiTriggerId;
        }

        private static List<BroadcastWorkbenchRuleDto> NormalizeBroadcastRules(IEnumerable<BroadcastWorkbenchRuleDto> rules)
        {
            List<BroadcastWorkbenchRuleDto> normalizedRules = new List<BroadcastWorkbenchRuleDto>();
            if (rules == null)
            {
                return normalizedRules;
            }

            foreach (BroadcastWorkbenchRuleDto rule in rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.id))
                {
                    continue;
                }

                BroadcastWorkbenchRuleDto normalizedRule = CloneBroadcastWorkbenchRule(rule);
                if (normalizedRule == null)
                {
                    continue;
                }

                normalizedRule.triggerId = NormalizeBroadcastTriggerId(normalizedRule.triggerId);
                if (string.IsNullOrEmpty(normalizedRule.triggerId))
                {
                    continue;
                }

                normalizedRule.nodes = normalizedRule.nodes
                    .Where(node => node != null
                        && !string.IsNullOrWhiteSpace(node.id)
                        && !string.IsNullOrWhiteSpace(node.type))
                    .ToArray();
                normalizedRules.Add(normalizedRule);
            }

            return normalizedRules;
        }

        private static string NormalizeBroadcastTriggerId(string triggerId)
        {
            switch ((triggerId ?? string.Empty).Trim())
            {
                case "approach_station":
                case "stop_and_open":
                case "leave_station":
                case "mid_route":
                case "bypass_waiting":
                    return triggerId.Trim();
                default:
                    return string.Empty;
            }
        }

        private DispatchWorkbenchLineDto CreateBroadcastWorkbenchLineDto(WorkbenchLineRuntime runtime)
        {
            if (runtime == null)
            {
                return new DispatchWorkbenchLineDto();
            }

            return new DispatchWorkbenchLineDto
            {
                id = runtime.Id,
                sourceLineId = runtime.Entity.Index.ToString(),
                name = runtime.Name,
                kind = runtime.Kind,
                direction = "up",
                stationCount = runtime.StationCount,
                color = runtime.Color,
                originStationId = runtime.OriginStationId,
                originStationName = runtime.OriginStationName,
                originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(runtime.Entity),
                maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(runtime.Entity),
                transportType = runtime.TransportType,
                allowedDepotId = GetWorkbenchAllowedDepotId(runtime.Entity)
            };
        }

        private void LogBroadcastWorkbenchException(string scope, Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            log.Info("[BroadcastWorkbenchException] " + scope + " -> " + DescribeWorkbenchException(ex));
        }
    }
}
