using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Colossal;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using SerializationContext = Colossal.Serialization.Entities.Context;
using Game;
using Game.Assets;
using Game.SceneFlow;
using RapidTransitMod.Broadcasting.WorkbenchBackend;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class AssetLifecycle
    {
        internal static AssetLifecycle Instance { get; private set; }
        private readonly object m_Sync = new object();
        private readonly List<SaveResult> m_Results = new List<SaveResult>();
        private readonly HashSet<string> m_DeletedSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Hash128, string> m_AssetPaths = new Dictionary<Hash128, string>();
        private Operation m_Operation;
        private bool m_CheckAll = true;
        private bool m_Stopped;
        private readonly Guid m_Updater;
        private State m_Current;
        private AssetStore.Cleanup m_Cleanup;
        private int m_Migrations;
        internal int Generation { get; private set; }
        internal string LastError { get; private set; } = string.Empty;
        internal int Deleted { get; private set; }
        internal int Failed { get; private set; }
        private bool m_ResultPending;
        private bool m_CleanupFinished;
        internal BroadcastAssetStorageResult Latest { get; private set; }
        internal UnregisteredAudio Unregistered { get; } = new UnregisteredAudio();
        internal bool StorageBusy => m_Operation != null || m_Migrations != 0;

        private AssetLifecycle()
        {
            AssetStore.Start();
            LastError = AssetStore.Error;
            Publish();
            if (GameManager.instance.gameMode == GameMode.Game)
                m_Current = ModRuntimeHostSystem.Instance?.m_AnnouncementWorkbench?.State;
            GameManager.instance.onGameSaveLoad += OnSave;
            GameManager.instance.onGamePreload += OnPreload;
            AssetDatabase.global.onAssetDatabaseChanged.Subscribe(OnAssetsChanged);
            m_Updater = MainThreadDispatcher.RegisterUpdater(ProcessPending);
        }

        internal static void Start()
        {
            Stop();
            Instance = new AssetLifecycle();
        }

        internal static void Stop()
        {
            AssetLifecycle instance = Instance;
            if (instance == null) return;
            lock (instance.m_Sync) instance.m_Stopped = true;
            GameManager.instance.onGameSaveLoad -= instance.OnSave;
            GameManager.instance.onGamePreload -= instance.OnPreload;
            AssetDatabase.global.onAssetDatabaseChanged.Unsubscribe(instance.OnAssetsChanged);
            MainThreadDispatcher.UnregisterUpdater(instance.m_Updater);
            instance.m_Current = null;
            instance.m_Cleanup = null;
            instance.Unregistered.Close();
            AssetStore.Stop();
            Instance = null;
        }

        internal void CaptureSave(State state)
        {
            try
            {
                lock (m_Sync)
                    if (m_Operation?.Saving == true)
                        m_Operation.References = state.CaptureAssets();
            }
            catch (Exception ex) { Report(ex); }
        }

        internal void Loaded(SerializationContext context, Workbench workbench)
        {
            try
            {
                m_Current = GameManager.instance.gameMode == GameMode.Game ? workbench.State : null;
                CurrentChanged(m_Current);
                if (context.purpose != Purpose.LoadGame || m_Current == null) return;
                if (AssetDatabase.global.TryGetAsset(context.instigatorGuid, out IAssetData asset))
                {
                    lock (m_Sync)
                    {
                        string path = SavePath(asset);
                        if (path.Length != 0)
                            m_Results.Add(new SaveResult { Path = path, References = m_Current.CaptureAssets() });
                    }
                }
                workbench.Persistence.MigrateLegacy();
            }
            catch (Exception ex) { Report(ex); }
        }

        internal void CurrentChanged(State state)
        {
            if (state != null) m_Current = state;
            m_Cleanup?.UpdateCurrent(CurrentUse());
            Unregistered.ProtectionChanged();
        }

        internal bool IsCurrent(State state, int generation)
            => !m_Stopped && generation == Generation && ReferenceEquals(m_Current, state);

        internal void BeginMigration()
        {
            m_Migrations++;
            Unregistered.ProtectionChanged();
        }

        internal void EndMigration()
        {
            m_Migrations--;
            Unregistered.ProtectionChanged();
        }

        internal void Report(Exception ex)
            => Report(ex.Message);

        internal void Report(string error)
        {
            LastError = error;
            m_ResultPending = true;
            Mod.log.Info("Broadcast asset operation failed: " + LastError);
        }

        private void OnSave(string name, string preview, bool start, bool success)
        {
            try
            {
                lock (m_Sync)
                {
                    if (m_Stopped) return;
                    if (start)
                    {
                        m_Operation = new Operation { Saving = true };
                        return;
                    }
                    if (!success || m_Operation == null) return;
                    SaveGameMetadata target = GameManager.instance.settings.userState.lastSaveGameMetadata;
                    if (target?.database?.dataSource?.isRemoteStorageSource == true)
                    {
                        m_Operation.Success = true;
                        return;
                    }
                    if (m_Operation.References == null)
                    {
                        m_Operation.BlockCleanup = true;
                        Report("Saved broadcast asset records are unavailable. File cleanup is disabled.");
                        return;
                    }
                    string path = SavePath(target);
                    if (path.Length != 0)
                    {
                        m_Results.Add(new SaveResult { Path = path, References = m_Operation.References, Clean = true });
                        m_Operation.Success = true;
                    }
                    else
                    {
                        m_Operation.BlockCleanup = true;
                        Report("Local save location is unavailable. File cleanup is disabled.");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (m_Sync)
                    if (m_Operation != null) m_Operation.BlockCleanup = true;
                Report(ex);
            }
        }

        private void OnPreload(Purpose purpose, GameMode mode)
        {
            lock (m_Sync)
            {
                if (m_Stopped) return;
                Generation++;
                Deleted = 0;
                Failed = 0;
                m_CleanupFinished = false;
                LastError = string.Empty;
                m_ResultPending = true;
                m_Operation = new Operation();
                m_Current = null;
                Unregistered.ProtectionChanged();
            }
        }

        private void OnAssetsChanged(AssetChangedEventArgs args)
        {
            try
            {
                lock (m_Sync)
                {
                    if (m_Stopped) return;
                    bool saveAsset = args.asset is SaveGameMetadata || args.asset is SaveGameData;
                    if (args.change == ChangeType.AssetDeleted && (saveAsset || args.asset is PackageAsset))
                    {
                        string path = SavePath(args.asset, true);
                        if (path.Length != 0)
                        {
                            m_DeletedSaves.Add(path);
                            m_AssetPaths.Remove(args.asset.id.guid);
                        }
                        else if (saveAsset && args.asset.database is ILocalAssetDatabase localSave
                            && !localSave.dataSource.isRemoteStorageSource)
                            m_CheckAll = true;
                        return;
                    }
                    if (args.change == ChangeType.BulkAssetsChange && saveAsset)
                    {
                        if (m_Operation?.Saving == true) return;
                        if (args.asset.database is ILocalAssetDatabase localSave && !localSave.dataSource.isRemoteStorageSource
                            && SavePath(args.asset, true).Length == 0)
                            m_CheckAll = true;
                        return;
                    }
                    if (args.change == ChangeType.DatabaseRegistered
                        && args.database is ILocalAssetDatabase local && !local.dataSource.isRemoteStorageSource)
                        m_CheckAll = true;
                    else if (args.change == ChangeType.BulkAssetsChange && args.asset == null
                        && args.database is ILocalAssetDatabase db && !db.dataSource.isRemoteStorageSource)
                        m_CheckAll = true;
                }
            }
            catch (Exception ex) { Report(ex); }
        }

        private bool ProcessPending()
        {
            try { return ProcessStorage(); }
            catch (Exception ex)
            {
                lock (m_Sync)
                {
                    m_Cleanup = null;
                    m_Results.Clear();
                    m_DeletedSaves.Clear();
                    m_CheckAll = false;
                    m_ResultPending = false;
                    Unregistered.Fail(ex);
                    LastError = AssetStore.DeleteError(ex);
                    Mod.log.Info("Broadcast audio cleanup processing failed.\n" + ex.ToString());
                    try { Publish(); }
                    catch (Exception publishError)
                    {
                        m_ResultPending = false;
                        Mod.log.Info("Broadcast audio cleanup result publication failed.\n" + publishError.ToString());
                    }
                }
                return false;
            }
        }

        private bool ProcessStorage()
        {
            lock (m_Sync)
            {
                if (m_Stopped) return true;
                Unregistered.Update(this);
                if (m_Operation != null)
                {
                    if (m_Operation.Completion == null)
                    {
                        try { m_Operation.Completion = TaskManager.instance.Complete("SaveLoadGame"); }
                        catch (Exception ex)
                        {
                            m_Operation = null;
                            m_Cleanup = null;
                            LastError = AssetStore.DeleteError(ex);
                            m_ResultPending = true;
                            Mod.log.Info("Broadcast audio cleanup completion failed.\n" + ex.ToString());
                            return false;
                        }
                    }
                    if (!m_Operation.Completion.IsCompleted) return false;
                    bool failedSave = m_Operation.Saving && !m_Operation.Success;
                    if (m_Operation.BlockCleanup) m_Cleanup = null;
                    m_Operation = null;
                    if (failedSave)
                    {
                        m_DeletedSaves.Clear();
                        m_CheckAll = false;
                    }
                }
                if (m_ResultPending) Publish();
                if (m_Results.Count == 0 && m_DeletedSaves.Count == 0 && !m_CheckAll && m_Cleanup == null)
                    return false;
                try
                {
                    bool changed = m_Results.Count != 0 || m_DeletedSaves.Count != 0 || m_CheckAll;
                    if (changed)
                    {
                        Unregistered.ProtectionChanged();
                        bool clean = false;
                        foreach (SaveResult result in m_Results)
                        {
                            AssetStore.SetReferences(result.Path, result.References);
                            clean |= result.Clean;
                        }
                        m_Results.Clear();
                        clean |= AssetStore.CheckSaves(m_DeletedSaves, m_CheckAll);
                        m_DeletedSaves.Clear();
                        m_CheckAll = false;
                        if (clean)
                        {
                            Deleted = 0;
                            Failed = 0;
                            m_CleanupFinished = false;
                        }
                        if (!AssetStore.Flush())
                        {
                            m_Cleanup = null;
                            LastError = AssetStore.Error;
                            Publish();
                        }
                        else if (clean || m_Cleanup != null) m_Cleanup = AssetStore.BeginCleanup(CurrentUse());
                        if (clean && m_Cleanup == null && AssetStore.CanClean)
                        {
                            m_CleanupFinished = true;
                            LastError = string.Empty;
                            Publish();
                        }
                    }
                    if (!AssetStore.CanClean) m_Cleanup = null;
                    if (m_Migrations != 0) return false;
                    if (!Unregistered.DeletedBatch && m_Cleanup != null && m_Cleanup.Step())
                    {
                        Deleted = m_Cleanup.Deleted;
                        Failed = m_Cleanup.Failed;
                        m_CleanupFinished = true;
                        AssetStore.Flush();
                        LastError = string.IsNullOrEmpty(m_Cleanup.Error) ? AssetStore.Error : m_Cleanup.Error;
                        string errorFile = m_Cleanup.ErrorFile;
                        m_Cleanup = null;
                        Publish(errorFile);
                        return false;
                    }
                    LastError = AssetStore.Error;
                }
                catch (Exception ex)
                {
                    m_Cleanup = null;
                    m_Results.Clear();
                    m_DeletedSaves.Clear();
                    m_CheckAll = false;
                    LastError = AssetStore.DeleteError(ex);
                    m_ResultPending = true;
                    Mod.log.Info("Broadcast audio cleanup failed.\n" + ex.ToString());
                }
                return false;
            }
        }

        private HashSet<string> CurrentUse()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m_Current != null)
                foreach (var mode in m_Current.CaptureAssets().Values)
                    foreach (string id in mode.Values) used.Add(id);
            return used;
        }

        internal HashSet<string> ProtectedPaths() => AssetStore.ProtectedPaths(CurrentUse());

        private void Publish(string errorFile = "")
        {
            Latest = new BroadcastAssetStorageResult { generation = Generation, state = !string.IsNullOrEmpty(LastError) ? "error" : m_CleanupFinished ? "completed" : string.Empty,
                deleted = Deleted, failed = Failed, errorFile = errorFile, error = string.IsNullOrEmpty(LastError) ? string.Empty
                    : LastError == "delete-busy" || LastError == "delete-access" ? LastError : "delete-failed" };
            m_ResultPending = false;
            Workbenches.UiEvents.Push(new BroadcastWorkbenchSnapshot { assetsOnly = true, assetStorage = Latest });
        }

        private string SavePath(IAssetData asset, bool deleted = false)
        {
            ILocalAssetDatabase database = asset?.database;
            if (database?.dataSource is not FileSystemDataSource || database.dataSource.isRemoteStorageSource)
                return string.Empty;
            if (!deleted)
            {
                SourceMeta meta = database.GetMeta(asset.id);
                if (meta.packaged) meta = database.GetMeta(meta.package);
                if (!string.IsNullOrEmpty(meta.path) && meta.path.EndsWith(".cok", StringComparison.OrdinalIgnoreCase))
                {
                    string path = AssetStore.LegacyPath(meta.path);
                    m_AssetPaths[asset.id.guid] = path;
                    return path;
                }
            }
            if (m_AssetPaths.TryGetValue(asset.id.guid, out string captured)) return captured;
            string uri = asset.id.uri;
            if (string.IsNullOrEmpty(uri)) return string.Empty;
            string relative = uri;
            int packaged = relative.IndexOf(".cok@", StringComparison.OrdinalIgnoreCase);
            if (packaged >= 0) relative = relative.Substring(0, packaged + 4);
            if (!relative.EndsWith(".cok", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string pathFromUri = AssetStore.LegacyPath(Path.Combine(database.dataSource.rootPath, relative));
            if (AssetStore.HasSave(pathFromUri)) return pathFromUri;
            string decoded = AssetStore.LegacyPath(Path.Combine(database.dataSource.rootPath, Uri.UnescapeDataString(relative)));
            return AssetStore.HasSave(decoded) ? decoded : string.Empty;
        }

        private sealed class Operation
        {
            internal bool Saving;
            internal bool Success;
            internal bool BlockCleanup;
            internal Task Completion;
            internal Dictionary<string, Dictionary<string, string>> References;
        }

        private sealed class SaveResult
        {
            internal bool Clean;
            internal string Path;
            internal Dictionary<string, Dictionary<string, string>> References;
        }
    }
}
