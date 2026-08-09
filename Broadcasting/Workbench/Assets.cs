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
    internal sealed class Assets : ModuleBase
    {
        internal Assets(Context context) : base(context) { }

                public string LoadBroadcastAssetBrowserJson(string requestJson)
                {
                    ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "loadBroadcastAssetBrowser");
                    using (UseScope(scope))
                    {
                        return global::RapidTransitMod.Workbenches.Json.Write(
                            Browser(Workbenches.ModeRequest.ReadPath(requestJson)));
                    }
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
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "importBroadcastExternalAssets");
                        using (UseScope(scope))
                        {
                        BroadcastWorkbenchImportExternalAssetsRequest request =
                            global::RapidTransitMod.Workbenches.Json.Read<BroadcastWorkbenchImportExternalAssetsRequest>(requestJson);
                        string[] selectedPaths = request?.selectedPaths ?? Array.Empty<string>();
                        if (selectedPaths.Length == 0)
                        {
                            result.error = "No files selected.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        string managedAssetDirectory = EnsureDir();
                        if (string.IsNullOrEmpty(managedAssetDirectory))
                        {
                            result.error = "Broadcast asset directory is unavailable.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        HashSet<string> allowedExtensions = new HashSet<string>(s_Extensions, StringComparer.OrdinalIgnoreCase);
                        HashSet<string> existingNames = new HashSet<string>(
                            Catalog
                                .Select(asset => asset?.name)
                                .Where(name => !string.IsNullOrWhiteSpace(name)),
                            StringComparer.OrdinalIgnoreCase);

                        List<BroadcastWorkbenchAssetDto> importedAssets = new List<BroadcastWorkbenchAssetDto>();
                        for (int i = 0; i < selectedPaths.Length; i++)
                        {
                            string normalizedFilePath = Path(selectedPaths[i]);
                            if (string.IsNullOrEmpty(normalizedFilePath))
                            {
                                continue;
                            }

                            string extension = IoPath.GetExtension(normalizedFilePath);
                            if (string.IsNullOrEmpty(extension) || !allowedExtensions.Contains(extension))
                            {
                                continue;
                            }

                            string destinationFileName = IoPath.GetFileName(normalizedFilePath);
                            if (string.IsNullOrEmpty(destinationFileName) || !existingNames.Add(destinationFileName))
                            {
                                continue;
                            }

                            string destinationPath = IoPath.Combine(managedAssetDirectory, destinationFileName);
                            if (!File.Exists(destinationPath))
                            {
                                File.Copy(normalizedFilePath, destinationPath, false);
                            }

                            importedAssets.Add(Dto(destinationPath));
                        }

                        if (importedAssets.Count == 0)
                        {
                            result.success = true;
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        Catalog.AddRange(importedAssets);
                        Catalog.Sort((left, right) =>
                            string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));

                        string normalizedCurrentPath = Dir(request?.currentPath);
                        if (!string.IsNullOrEmpty(normalizedCurrentPath))
                        {
                            BrowseFolder = normalizedCurrentPath;
                        }

                        AssetFolder = managedAssetDirectory;
                        PendingConflicts.Clear();
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;
                        result.importedCount = importedAssets.Count;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, string.Empty));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("ImportBroadcastExternalAssetsJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string DeleteBroadcastAssetJson(string requestJson)
                {
                    BroadcastWorkbenchDeleteAssetResult result = new BroadcastWorkbenchDeleteAssetResult
                    {
                        success = false,
                        error = string.Empty
                    };
                    List<BroadcastWorkbenchAssetDto> catalogSnapshot = null;

                    try
                    {
                        LoadWorkbench();
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "deleteBroadcastAsset");
                        using (UseScope(scope))
                        {
                        catalogSnapshot = Catalog.Select(CloneAsset).ToList();
                        string normalizedAssetName = Workbenches.ModeRequest.ReadAssetName(requestJson)?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(normalizedAssetName))
                        {
                            result.error = "Asset name is missing.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        if (HasAppliedRefs(scope, normalizedAssetName))
                        {
                            result.error = "broadcast-asset-in-use";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        if (!Remove(normalizedAssetName))
                        {
                            result.error = "Selected asset was not found.";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        PendingConflicts.Clear();
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, string.Empty));
                        }
                    }
                    catch (Exception ex)
                    {
                        RestoreCatalogSnapshot(catalogSnapshot);
                        result.error = ex.Message ?? string.Empty;
                        LogException("DeleteBroadcastAssetJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string DeleteAllBroadcastAssetsJson(string requestJson)
                {
                    BroadcastWorkbenchDeleteAllAssetsResult result = new BroadcastWorkbenchDeleteAllAssetsResult
                    {
                        success = false,
                        error = string.Empty
                    };
                    List<BroadcastWorkbenchAssetDto> catalogSnapshot = null;

                    try
                    {
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "deleteAllBroadcastAssets");
                        using (UseScope(scope))
                        {
                        LoadWorkbench();
                        catalogSnapshot = Catalog.Select(CloneAsset).ToList();
                        if (HasAnyAppliedRefs(scope))
                        {
                            result.error = "broadcast-asset-in-use";
                            return global::RapidTransitMod.Workbenches.Json.Write(result);
                        }

                        RemoveAll();
                        PendingConflicts.Clear();
                        IncrementWorkbenchSnapshotVersion();
                        SaveWorkbench();
                        result.success = true;

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, string.Empty));
                        }
                    }
                    catch (Exception ex)
                    {
                        RestoreCatalogSnapshot(catalogSnapshot);
                        result.error = ex.Message ?? string.Empty;
                        LogException("DeleteAllBroadcastAssetsJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                public string OpenBroadcastAssetDirectoryPickerJson(string requestJson)
                {
                    BroadcastWorkbenchDirectoryPickerResult result = new BroadcastWorkbenchDirectoryPickerResult
                    {
                        success = false,
                        pending = false,
                        error = string.Empty
                    };

                    try
                    {
                        ModeScope scope = Workbenches.ModeRequest.ReadBroadcastScope(requestJson, "openBroadcastAssetDirectoryPicker");
                        MainThreadDispatcher.RunOnMainThread(() =>
                        {
                            using (UseScope(scope))
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
                                optionsSystem.OpenDirectoryBrowser(Root(), directory =>
                                {
                                    try
                                    {
                                        SelectDir(scope, directory);
                                        gameScreenSystem.activeScreen = previousScreen;
                                    }
                                    catch (Exception ex)
                                    {
                                        LogException("SelectDir", ex);
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                LogException("OpenBroadcastAssetDirectoryPicker", ex);
                            }
                            }
                        });

                        result.success = true;
                        result.pending = true;
                    }
                    catch (Exception ex)
                    {
                        result.error = ex.Message ?? string.Empty;
                        LogException("OpenBroadcastAssetDirectoryPickerJson", ex);
                    }

                    return global::RapidTransitMod.Workbenches.Json.Write(result);
                }

                internal BroadcastWorkbenchExternalAssetBrowserSnapshot Browser(string requestedPath)
                {
                    if (string.Equals(requestedPath, DrivesToken, StringComparison.Ordinal))
                    {
                        return Drives();
                    }

                    string startPath = StartPath();
                    string currentPath = Dir(requestedPath);
                    if (string.IsNullOrEmpty(currentPath))
                    {
                        currentPath = startPath;
                    }

                    string rootPath = RootPath(currentPath, startPath);
                    string parentPath = ParentPath(currentPath, rootPath);

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

                        HashSet<string> allowedExtensions = new HashSet<string>(s_Extensions, StringComparer.OrdinalIgnoreCase);
                        FileInfo[] candidateFiles = directoryInfo.GetFiles();
                        for (int i = 0; i < candidateFiles.Length; i++)
                        {
                            FileInfo file = candidateFiles[i];
                            string extension = IoPath.GetExtension(file.FullName);
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
                        LogException("Browser", ex);
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
                        allowedExtensions = s_Extensions.ToArray(),
                        error = error
                    };
                }

                internal BroadcastWorkbenchExternalAssetBrowserSnapshot Drives()
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
                        LogException("Drives", ex);
                    }

                    folders.Sort(StringComparer.OrdinalIgnoreCase);

                    return new BroadcastWorkbenchExternalAssetBrowserSnapshot
                    {
                        rootPath = string.Empty,
                        currentPath = string.Empty,
                        parentPath = string.Empty,
                        folders = folders.ToArray(),
                        files = Array.Empty<BroadcastWorkbenchExternalAssetFileDto>(),
                        allowedExtensions = s_Extensions.ToArray(),
                        error = string.Empty
                    };
                }

                internal void SelectDir(string directory)
                {
                    SelectDir(CurrentScope, directory);
                }

                internal void SelectDir(ModeScope scope, string directory)
                {
                    string normalizedDirectory = Dir(directory);
                    if (string.IsNullOrEmpty(normalizedDirectory))
                    {
                        return;
                    }

                    using (UseScope(scope))
                    {
                        AssetFolder = normalizedDirectory;
                        Dictionary<string, BroadcastWorkbenchAssetDto> scannedByName = Scan(normalizedDirectory)
                            .Where(asset => asset != null && !string.IsNullOrWhiteSpace(asset.name))
                            .GroupBy(asset => asset.name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => CloneAsset(group.First()), StringComparer.OrdinalIgnoreCase);
                        List<BroadcastWorkbenchAssetDto> refreshedCatalog = new List<BroadcastWorkbenchAssetDto>();
                        for (int i = 0; i < Catalog.Count; i++)
                        {
                            BroadcastWorkbenchAssetDto current = Catalog[i];
                            if (current == null || string.IsNullOrWhiteSpace(current.name))
                            {
                                continue;
                            }

                            if (scannedByName.TryGetValue(current.name, out BroadcastWorkbenchAssetDto scanned))
                            {
                                refreshedCatalog.Add(CloneAsset(scanned));
                                continue;
                            }

                            refreshedCatalog.Add(new BroadcastWorkbenchAssetDto
                            {
                                name = current.name ?? string.Empty,
                                desc = current.desc ?? string.Empty,
                                length = current.length ?? string.Empty,
                                path = string.Empty,
                                extension = current.extension ?? string.Empty,
                                missing = true
                            });
                        }

                        Catalog.Clear();
                        Catalog.AddRange(refreshedCatalog.OrderBy(asset => asset?.name, StringComparer.OrdinalIgnoreCase));

                        global::RapidTransitMod.Workbenches.UiEvents.Push(
                            m_Ctx.Snapshot.Build(scope, string.Empty));
                    }
                }

                internal string Root()
                {
                    string normalizedDirectory = Dir(AssetFolder);
                    if (!string.IsNullOrEmpty(normalizedDirectory))
                    {
                        return normalizedDirectory;
                    }

                    return string.Empty;
                }

                internal static string Dir(string directory)
                {
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        return string.Empty;
                    }

                    try
                    {
                        string fullPath = IoPath.GetFullPath(directory);
                        return Directory.Exists(fullPath) ? NormalizeDirectoryBrowserPath(fullPath) : string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }

                internal static IEnumerable<BroadcastWorkbenchAssetDto> Scan(string directory)
                {
                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    {
                        return Array.Empty<BroadcastWorkbenchAssetDto>();
                    }

                    HashSet<string> extensions = new HashSet<string>(s_Extensions, StringComparer.OrdinalIgnoreCase);
                    List<BroadcastWorkbenchAssetDto> assets = new List<BroadcastWorkbenchAssetDto>();

                    try
                    {
                        string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                        for (int i = 0; i < files.Length; i++)
                        {
                            string filePath = files[i];
                            string extension = IoPath.GetExtension(filePath);
                            if (string.IsNullOrEmpty(extension) || !extensions.Contains(extension))
                            {
                                continue;
                            }

                            string fileName = IoPath.GetFileName(filePath);
                            assets.Add(Dto(filePath));
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Info("[BroadcastWorkbench] Scan asset directory failed: " + ex.Message);
                    }

                    assets.Sort((left, right) => string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase));
                    return assets;
                }

                internal string StartPath()
                {
                    string existingDirectory = Dir(BrowseFolder);
                    if (!string.IsNullOrEmpty(existingDirectory))
                    {
                        return existingDirectory;
                    }

                    string documentsDirectory = Dir(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                    if (!string.IsNullOrEmpty(documentsDirectory))
                    {
                        return documentsDirectory;
                    }

                    string currentDirectory = Dir(Environment.CurrentDirectory);
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

                internal static string RootPath(string currentPath, string fallbackPath)
                {
                    string rootPath = NormalizeFileBrowserPath(IoPath.GetPathRoot(currentPath));
                    if (!string.IsNullOrEmpty(rootPath))
                    {
                        return rootPath;
                    }

                    return NormalizeFileBrowserPath(IoPath.GetPathRoot(fallbackPath));
                }

                internal static string ParentPath(string currentPath, string rootPath)
                {
                    string normalizedCurrentPath = NormalizeFileBrowserPath(currentPath);
                    string normalizedRootPath = NormalizeFileBrowserPath(rootPath);
                    if (string.IsNullOrEmpty(normalizedCurrentPath) || string.IsNullOrEmpty(normalizedRootPath))
                    {
                        return string.Empty;
                    }

                    if (string.Equals(normalizedCurrentPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return DrivesToken;
                    }

                    string currentPathWithoutTrailingSlash = TrimEndingDirectorySeparator(normalizedCurrentPath);
                    DirectoryInfo parent = Directory.GetParent(currentPathWithoutTrailingSlash);
                    return NormalizeDirectoryBrowserPath(parent?.FullName);
                }

                internal static string Path(string filePath)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return string.Empty;
                    }

                    try
                    {
                        string fullPath = IoPath.GetFullPath(filePath);
                        return File.Exists(fullPath) ? NormalizeFileBrowserPath(fullPath) : string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }

                internal static string NormalizeFileBrowserPath(string path)
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

                internal static string NormalizeDirectoryBrowserPath(string path)
                {
                    string normalized = NormalizeFileBrowserPath(path);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        return string.Empty;
                    }

                    return normalized.EndsWith("\\", StringComparison.Ordinal) ? normalized : normalized + "\\";
                }

                internal static string TrimEndingDirectorySeparator(string path)
                {
                    string normalized = NormalizeFileBrowserPath(path);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        return string.Empty;
                    }

                    string rootPath = NormalizeFileBrowserPath(IoPath.GetPathRoot(normalized));
                    if (!string.IsNullOrEmpty(rootPath)
                        && string.Equals(normalized, rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return normalized;
                    }

                    return normalized.TrimEnd('\\');
                }

                internal static BroadcastWorkbenchAssetDto Dto(string filePath)
                {
                    string normalizedFilePath = Path(filePath);
                    string extension = IoPath.GetExtension(normalizedFilePath);
                    string fileName = IoPath.GetFileName(normalizedFilePath);
                    return new BroadcastWorkbenchAssetDto
                    {
                        name = fileName ?? string.Empty,
                        desc = extension.TrimStart('.').ToUpperInvariant(),
                        length = Length(normalizedFilePath, extension),
                        path = normalizedFilePath,
                        extension = extension ?? string.Empty,
                        missing = string.IsNullOrEmpty(normalizedFilePath)
                    };
                }

                internal static string Length(string filePath, string extension)
                {
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        return string.Empty;
                    }

                    try
                    {
                        using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        Track track = new Track(stream, extension ?? string.Empty);
                        return FormatLength(track.DurationMs);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }

                internal static string FormatLength(double durationMs)
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

                internal string EnsureDir()
                {
                    return new AssetScope(CurrentScope).EnsureDir();
                }

                internal bool Remove(string assetName)
                {
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        return false;
                    }

                    bool removed = false;
                    string normalizedAssetName = assetName.Trim();

                    ModeScope scope = CurrentScope;
                    MainThreadDispatcher.RunOnMainThread(() =>
                        m_Ctx.Preview.StopAsset(normalizedAssetName, notify: true, modeToken: scope.Token));

                    for (int i = Catalog.Count - 1; i >= 0; i--)
                    {
                        BroadcastWorkbenchAssetDto asset = Catalog[i];
                        if (!string.Equals(asset?.name, normalizedAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Keep broadcast source files on disk for now; deletion only mutates save state.

                        Catalog.RemoveAt(i);
                        removed = true;
                    }

                    if (!removed)
                    {
                        return false;
                    }

                    MainThreadDispatcher.RunOnMainThread(() => m_Announcements.RemoveAsset(scope, normalizedAssetName));
                    return true;
                }

                internal void RemoveAll()
                {
                    ModeScope scope = CurrentScope;
                    MainThreadDispatcher.RunOnMainThread(() =>
                        m_Ctx.Preview.StopAsset(string.Empty, notify: true, modeToken: scope.Token));

                    // Keep broadcast source files on disk for now; deletion only mutates save state.
                    Catalog.Clear();
                    MainThreadDispatcher.RunOnMainThread(() => m_Announcements.RemoveAllAssets(scope));
                }

                private void RestoreCatalogSnapshot(List<BroadcastWorkbenchAssetDto> snapshot)
                {
                    if (snapshot == null)
                    {
                        return;
                    }

                    Catalog.Clear();
                    Catalog.AddRange(snapshot.Select(CloneAsset));
                }

                internal bool HasCatalogAsset(string assetName)
                {
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        return false;
                    }

                    return Catalog.Any(asset =>
                        string.Equals(asset?.name, assetName, StringComparison.OrdinalIgnoreCase));
                }

                internal bool HasUsableAsset(string assetName)
                {
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        return false;
                    }

                    return Catalog.Any(asset =>
                        string.Equals(asset?.name, assetName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(Path(asset?.path)));
                }

                private bool HasAnyAppliedRefs(ModeScope scope)
                {
                    HashSet<string> assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < Catalog.Count; i++)
                    {
                        string assetName = Catalog[i]?.name?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(assetName))
                        {
                            assetNames.Add(assetName);
                        }
                    }

                    if (assetNames.Count == 0)
                    {
                        return false;
                    }

                    foreach (string assetName in assetNames)
                    {
                        if (HasAppliedRefs(scope, assetName))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                private bool HasAppliedRefs(ModeScope scope, string assetName)
                {
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        return false;
                    }

                    return HasBindingRefs(scope, AppliedBindings, assetName)
                        || HasRuleRefs(scope, AppliedRules, assetName)
                        || HasPlatformRefs(scope, AppliedPlatforms, assetName);
                }

                private static bool HasBindingRefs(
                    ModeScope scope,
                    Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> allBindings,
                    string assetName)
                {
                    foreach (KeyValuePair<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> lineEntry in allBindings)
                    {
                        if (!MatchesAppliedLineScope(scope, lineEntry.Key))
                        {
                            continue;
                        }

                        Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> bindings = lineEntry.Value;
                        if (bindings == null)
                        {
                            continue;
                        }

                        foreach (KeyValuePair<string, List<BroadcastWorkbenchStationBindingDto>> stationEntry in bindings)
                        {
                            if ((stationEntry.Value ?? new List<BroadcastWorkbenchStationBindingDto>())
                                .Any(binding => string.Equals(binding?.assetName, assetName, StringComparison.OrdinalIgnoreCase)))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                private static bool HasRuleRefs(
                    ModeScope scope,
                    Dictionary<string, List<BroadcastWorkbenchRuleDto>> allRules,
                    string assetName)
                {
                    foreach (KeyValuePair<string, List<BroadcastWorkbenchRuleDto>> lineEntry in allRules)
                    {
                        if (!MatchesAppliedLineScope(scope, lineEntry.Key))
                        {
                            continue;
                        }

                        List<BroadcastWorkbenchRuleDto> rules = lineEntry.Value;
                        if (rules == null)
                        {
                            continue;
                        }

                        for (int i = 0; i < rules.Count; i++)
                        {
                            if (HasAssetNode(rules[i]?.nodes, assetName))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                private static bool HasPlatformRefs(
                    ModeScope scope,
                    Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> allAnnouncements,
                    string assetName)
                {
                    foreach (KeyValuePair<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> lineEntry in allAnnouncements)
                    {
                        if (!MatchesAppliedLineScope(scope, lineEntry.Key))
                        {
                            continue;
                        }

                        Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> announcements = lineEntry.Value;
                        if (announcements == null)
                        {
                            continue;
                        }

                        foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> stationEntry in announcements)
                        {
                            if (HasAssetNode(stationEntry.Value?.nodes, assetName))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                private static bool HasAssetNode(BroadcastWorkbenchRuleNodeDto[] nodes, string assetName)
                {
                    if (nodes == null || nodes.Length == 0)
                    {
                        return false;
                    }

                    for (int i = 0; i < nodes.Length; i++)
                    {
                        BroadcastWorkbenchRuleNodeDto node = nodes[i];
                        if (node != null
                            && string.Equals(node.type, "asset", StringComparison.Ordinal)
                            && string.Equals(node.name, assetName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                private static bool MatchesAppliedLineScope(ModeScope scope, string lineId)
                {
                    if (string.IsNullOrWhiteSpace(lineId))
                    {
                        return false;
                    }

                    if (LineIdentityService.TryGetMode(lineId, out TransitMode mode) && mode != TransitMode.Unknown)
                    {
                        return mode == scope.Mode;
                    }

                    return lineId.IndexOf(':') < 0 && scope.Mode == ModeScope.DefaultWorkbench.Mode;
                }

                internal static BroadcastWorkbenchAssetDto CloneAsset(BroadcastWorkbenchAssetDto asset)
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
                        extension = asset.extension ?? string.Empty,
                        missing = asset.missing
                    };
                }
    }
}
