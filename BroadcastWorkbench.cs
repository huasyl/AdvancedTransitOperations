using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Colossal.Core;
using Game.UI.InGame;
using Game.UI.Menu;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private static readonly string[] s_BroadcastAssetExtensions = { ".wav", ".mp3" };
        private readonly List<BroadcastWorkbenchAssetDto> m_BroadcastAssetCatalog = new List<BroadcastWorkbenchAssetDto>();
        private string m_BroadcastAssetDirectory = string.Empty;

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
            public string assetDirectory;
            [DataMember]
            public BroadcastWorkbenchAssetDto[] assets;
            [DataMember]
            public string version;
            [DataMember]
            public string sourceMode;
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

        private BroadcastWorkbenchSnapshot BuildBroadcastWorkbenchSnapshot(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();

            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            WorkbenchLineRuntime activeRuntime = runtimeLines.Count > 0
                ? ResolveActiveWorkbenchLine(runtimeLines, preferredLineId)
                : null;
            List<DispatchWorkbenchStationDto> stations = activeRuntime != null
                ? BuildWorkbenchStations(activeRuntime.Entity)
                : new List<DispatchWorkbenchStationDto>();

            return new BroadcastWorkbenchSnapshot
            {
                selectedLineId = activeRuntime?.Id ?? string.Empty,
                lines = runtimeLines.Select(CreateBroadcastWorkbenchLineDto).ToArray(),
                stations = stations.ToArray(),
                assetDirectory = m_BroadcastAssetDirectory,
                assets = m_BroadcastAssetCatalog.Select(CloneBroadcastWorkbenchAsset).ToArray(),
                version = m_WorkbenchSnapshotVersion.ToString(),
                sourceMode = "game-backend"
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
                return Directory.Exists(fullPath) ? fullPath : string.Empty;
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
                    assets.Add(new BroadcastWorkbenchAssetDto
                    {
                        name = fileName ?? string.Empty,
                        desc = extension.TrimStart('.').ToUpperInvariant(),
                        length = string.Empty,
                        path = filePath,
                        extension = extension
                    });
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("[BroadcastWorkbench] Scan asset directory failed: " + ex.Message);
            }

            assets.Sort((left, right) => string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));
            return assets;
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
