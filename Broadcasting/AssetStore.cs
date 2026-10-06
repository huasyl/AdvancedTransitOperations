using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using RapidTransitMod.Broadcasting.WorkbenchBackend;
using IoPath = System.IO.Path;

namespace RapidTransitMod.Broadcasting
{
    internal static class AssetStore
    {
        private static ReferenceIndex s_Index;
        private static bool s_Dirty;
        private static bool s_WriteFailed;
        private static string s_Error = string.Empty;
        internal static string Error => s_Error;
        internal static bool CanClean => s_Index != null && !s_WriteFailed;
        internal static bool HasSave(string path) => s_Index != null && s_Index.saves.ContainsKey(path);
        internal static bool HasFile(string id) => s_Index != null && s_Index.files.ContainsKey(id);
        internal static int ProtectionVersion { get; private set; }

        internal static void Start()
        {
            ProtectionVersion++;
            s_Dirty = false;
            s_WriteFailed = false;
            s_Error = string.Empty;
            try
            {
                string path = IoPath.Combine(AssetScope.RootDir(), "references.json");
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    s_Index = (ReferenceIndex)Serializer().ReadObject(stream);
                }
                catch (FileNotFoundException) { s_Index = new ReferenceIndex(); }
                catch (DirectoryNotFoundException) { s_Index = new ReferenceIndex(); }
                if (s_Index == null || s_Index.version != 1 || s_Index.files == null || s_Index.saves == null)
                { InvalidIndex("Invalid broadcast index. File cleanup is disabled."); return; }
                foreach (var file in s_Index.files)
                {
                    if (!ValidId(file.Key) || file.Value?.sources == null)
                    { InvalidIndex("Invalid broadcast file record. File cleanup is disabled."); return; }
                    foreach (string source in file.Value.sources)
                        if (ManagedPath(source).Length == 0)
                        { InvalidIndex("Invalid broadcast source record. File cleanup is disabled."); return; }
                }
                foreach (var save in s_Index.saves)
                {
                    if (!IoPath.IsPathRooted(save.Key) || save.Value == null)
                    { InvalidIndex("Invalid broadcast save record. File cleanup is disabled."); return; }
                    foreach (var mode in save.Value)
                    {
                        if (mode.Value == null)
                        { InvalidIndex("Invalid broadcast asset mapping. File cleanup is disabled."); return; }
                        foreach (var asset in mode.Value)
                            if (string.IsNullOrEmpty(asset.Key) || string.IsNullOrEmpty(asset.Value)
                                || !ValidId(asset.Value) && !IoPath.IsPathRooted(asset.Value))
                            { InvalidIndex("Invalid broadcast asset identity. File cleanup is disabled."); return; }
                    }
                }
            }
            catch (Exception ex)
            {
                s_Index = null;
                Fail(ex);
            }
        }

        internal static void Stop()
        {
            s_Index = null;
            ProtectionVersion++;
        }

        internal static void Register(string assetId, string source = null, bool newImport = false)
        {
            if (s_Index == null || !ValidId(assetId)) return;
            if (!s_Index.files.TryGetValue(assetId, out FileRecord record))
            {
                s_Index.files[assetId] = record = new FileRecord { allowAutoDelete = newImport && string.IsNullOrEmpty(source) };
                s_Dirty = true;
                ProtectionVersion++;
            }
            if (!string.IsNullOrEmpty(source) && record.allowAutoDelete)
            {
                record.allowAutoDelete = false;
                s_Dirty = true;
                ProtectionVersion++;
            }
            string managedSource = ManagedPath(source);
            if (managedSource.Length != 0 && !string.Equals(managedSource, FilePath(assetId), StringComparison.OrdinalIgnoreCase)
                && !record.sources.Contains(managedSource))
            {
                record.sources.Add(managedSource);
                s_Dirty = true;
                ProtectionVersion++;
            }
        }

        internal static void SetReferences(string savePath, Dictionary<string, Dictionary<string, string>> references)
        {
            if (s_Index == null || string.IsNullOrEmpty(savePath)) return;
            foreach (var mode in references.Values)
                foreach (string id in mode.Values)
                    if (ValidId(id)) Register(id);
            if (s_Index.saves.TryGetValue(savePath, out var old) && Equal(old, references)) return;
            s_Index.saves[savePath] = references;
            s_Dirty = true;
            ProtectionVersion++;
        }

        internal static bool CheckSaves(HashSet<string> paths, bool all)
        {
            if (s_Index == null) return false;
            var removed = new List<string>();
            IEnumerable<string> candidates = all ? (IEnumerable<string>)s_Index.saves.Keys : paths;
            foreach (string path in candidates)
            {
                if (!s_Index.saves.ContainsKey(path)) continue;
                try { File.GetAttributes(path); }
                catch (FileNotFoundException) { removed.Add(path); }
                catch (DirectoryNotFoundException) { removed.Add(path); }
                catch (Exception ex)
                {
                    s_Error = ex.Message;
                    Mod.log.Info("Broadcast cleanup save check failed. Path: " + path + "\n" + ex.ToString());
                }
            }
            foreach (string path in removed)
            {
                s_Index.saves.Remove(path);
                s_Dirty = true;
                ProtectionVersion++;
            }
            return removed.Count != 0;
        }

        internal static bool Flush()
        {
            if (s_Index == null) return false;
            if (!s_Dirty) return true;
            string root = AssetScope.RootDir();
            string path = IoPath.Combine(root, "references.json");
            string temporary = IoPath.Combine(root, ".references-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                using (FileStream stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Serializer().WriteObject(stream, s_Index);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                s_Dirty = false;
                s_WriteFailed = false;
                s_Error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                s_WriteFailed = true;
                s_Error = ex.Message;
                Mod.log.Info("Broadcast asset index write failed. Path: " + path + "\n" + ex.ToString());
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex)
                {
                    s_Error = ex.Message;
                    Mod.log.Info("Broadcast asset temporary file deletion failed. Path: " + temporary + "\n" + ex.ToString());
                }
            }
        }

        internal static Cleanup BeginCleanup(HashSet<string> current)
        {
            if (s_Index == null || s_Dirty) return null;
            HashSet<string> protectedPaths = ProtectedPaths(current, false);
            var candidates = new List<DeleteItem>();
            foreach (var file in s_Index.files)
            {
                if (!file.Value.allowAutoDelete || protectedPaths.Contains(FilePath(file.Key))) continue;
                candidates.Add(new DeleteItem { Id = file.Key, Path = FilePath(file.Key), Last = file.Value.sources.Count == 0 });
                for (int i = 0; i < file.Value.sources.Count; i++)
                    candidates.Add(new DeleteItem { Id = file.Key, Path = file.Value.sources[i], Last = i == file.Value.sources.Count - 1 });
            }
            return candidates.Count == 0 ? null : new Cleanup(candidates, protectedPaths);
        }

        internal static Cleanup BeginManual(List<string> files, HashSet<string> protectedPaths, Action<string> removed)
        {
            if (!CanClean) return null;
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files) listed[LegacyPath(file)] = file;
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<DeleteItem>();
            foreach (var file in s_Index.files)
            {
                string root = FilePath(file.Key);
                bool included = listed.ContainsKey(root);
                foreach (string source in file.Value.sources) included |= listed.ContainsKey(source);
                if (!included) continue;
                var members = new List<string> { root };
                members.AddRange(file.Value.sources);
                for (int i = 0; i < members.Count; i++)
                {
                    string path = members[i];
                    bool shown = listed.TryGetValue(path, out string display);
                    candidates.Add(new DeleteItem { Id = file.Key, Path = shown ? display : path, CheckOnly = !shown,
                        Last = i == members.Count - 1, SourceCount = file.Value.sources.Count });
                    covered.Add(path);
                }
            }
            foreach (var file in listed)
                if (!covered.Contains(file.Key)) candidates.Add(new DeleteItem { Id = string.Empty, Path = file.Value });
            return candidates.Count == 0 ? null : new Cleanup(candidates, protectedPaths, true, removed);
        }

        internal sealed class Cleanup
        {
            private readonly List<DeleteItem> m_Candidates;
            private HashSet<string> m_ProtectedPaths;
            private readonly HashSet<string> m_Incomplete = new HashSet<string>(StringComparer.Ordinal);
            private readonly bool m_Manual;
            private readonly Action<string> m_Removed;
            private int m_Position;
            internal int Deleted { get; private set; }
            internal int Failed { get; private set; }
            internal string Error { get; private set; } = string.Empty;
            internal string ErrorFile { get; private set; } = string.Empty;

            internal Cleanup(List<DeleteItem> candidates, HashSet<string> protectedPaths, bool manual = false, Action<string> removed = null)
            {
                m_Candidates = candidates;
                m_ProtectedPaths = protectedPaths;
                m_Manual = manual;
                m_Removed = removed;
            }

            internal void UpdateProtection(HashSet<string> paths) => m_ProtectedPaths = paths;

            internal void UpdateCurrent(HashSet<string> current)
            {
                m_ProtectedPaths = ProtectedPaths(current, m_Manual);
            }

            internal bool Step()
            {
                int limit = Math.Min(m_Position + 4, m_Candidates.Count);
                while (m_Position < limit)
                {
                    DeleteItem item = m_Candidates[m_Position++];
                    s_Index.files.TryGetValue(item.Id, out FileRecord record);
                    bool retained = m_ProtectedPaths.Contains(LegacyPath(item.Path))
                        || item.Id.Length != 0 && (record == null || record.allowAutoDelete == m_Manual);
                    bool success = !retained && Delete(item.Path, item.CheckOnly);
                    if (!success || m_Manual && item.Last && record.sources.Count != item.SourceCount) m_Incomplete.Add(item.Id);
                    if (m_Manual && !item.CheckOnly && (retained || success)) m_Removed(item.Path);
                    if (item.Last && item.Id.Length != 0 && !m_Incomplete.Contains(item.Id))
                    {
                        s_Index.files.Remove(item.Id);
                        s_Dirty = true;
                        ProtectionVersion++;
                    }
                }
                return m_Position >= m_Candidates.Count;
            }

            private bool Delete(string path, bool checkOnly)
            {
                if (TryDelete(path, out bool deleted, out string error, checkOnly))
                {
                    if (deleted) Deleted++;
                    return true;
                }
                if (error.Length != 0)
                {
                    Failed++;
                    if (Error.Length == 0)
                    {
                        Error = error;
                        ErrorFile = IoPath.GetFileName(path);
                    }
                }
                return false;
            }
        }

        internal static HashSet<string> ProtectedPaths(HashSet<string> current, bool manual = true)
        {
            if (!CanClean) return null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var save in s_Index.saves.Values)
                foreach (var mode in save.Values)
                    foreach (string id in mode.Values)
                        paths.Add(ValidId(id) ? FilePath(id) : LegacyPath(id));
            foreach (string id in current) paths.Add(ValidId(id) ? FilePath(id) : LegacyPath(id));
            bool changed;
            do
            {
                changed = false;
                foreach (var file in s_Index.files)
                {
                    bool retained = file.Value.allowAutoDelete == manual || paths.Contains(FilePath(file.Key));
                    foreach (string source in file.Value.sources) retained |= paths.Contains(source);
                    if (!retained) continue;
                    changed |= paths.Add(FilePath(file.Key));
                    foreach (string source in file.Value.sources) changed |= paths.Add(source);
                }
            }
            while (changed);
            return paths;
        }

        internal static List<string> EnumerateAudio(string root)
        {
            var files = new List<string>();
            foreach (string directory in new[] { root, IoPath.Combine(root, "train"), IoPath.Combine(root, "subway"),
                IoPath.Combine(root, "tram"), IoPath.Combine(root, "bus") })
            {
                try
                {
                    if (HasLink(directory, root)) continue;
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            if (IsAudio(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) files.Add(file);
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        internal static bool TryDelete(string path, out bool deleted, out string error, bool checkOnly = false)
        {
            deleted = false;
            error = string.Empty;
            try
            {
                string managed = ManagedPath(path);
                if (checkOnly && managed.Length != 0)
                {
                    File.GetAttributes(managed);
                    return false;
                }
                if (managed.Length == 0 || HasLink(IoPath.GetDirectoryName(managed), AssetScope.RootDir()))
                { error = "delete-failed"; return false; }
                FileAttributes attributes = File.GetAttributes(managed);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                { error = "delete-failed"; return false; }
                File.Delete(managed);
                deleted = true;
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex)
            {
                error = DeleteError(ex);
                Mod.log.Info((checkOnly ? "Broadcast audio cleanup file check failed. Path: " : "Broadcast audio deletion failed. Path: ")
                    + path + "\n" + ex.ToString());
                return false;
            }
        }

        internal static string DeleteError(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "delete-access";
            if (ex is IOException && (ex.HResult == unchecked((int)0x80070020)
                || ex.HResult == unchecked((int)0x80070021))) return "delete-busy";
            return "delete-failed";
        }

        private static bool HasLink(string directory, string root)
        {
            string boundary = IoPath.GetFullPath(root).TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);
            for (string next = IoPath.GetFullPath(directory); ; next = IoPath.GetDirectoryName(next))
            {
                if ((File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(next, boundary, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrEmpty(next)) return true;
            }
        }

        private static bool IsAudio(string path)
        {
            string ext = IoPath.GetExtension(path);
            return string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase);
        }

        internal sealed class DeleteItem
        {
            internal string Id;
            internal string Path;
            internal bool Last;
            internal bool CheckOnly;
            internal int SourceCount;
        }

        internal static string LegacyPath(string path)
            => string.IsNullOrEmpty(path) ? string.Empty : IoPath.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();

        private static string ManagedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string full = LegacyPath(path);
            string root = LegacyPath(AssetScope.RootDir()).TrimEnd('/') + "/";
            string ext = IoPath.GetExtension(full);
            return full.StartsWith(root, StringComparison.Ordinal) && (ext == ".wav" || ext == ".mp3" || ext == ".ogg") ? full : string.Empty;
        }

        private static string FilePath(string id) => LegacyPath(IoPath.Combine(AssetScope.RootDir(), id));

        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(ReferenceIndex),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        private static bool Equal(Dictionary<string, Dictionary<string, string>> left, Dictionary<string, Dictionary<string, string>> right)
        {
            if (left.Count != right.Count) return false;
            foreach (var mode in right)
            {
                if (!left.TryGetValue(mode.Key, out var names) || names.Count != mode.Value.Count) return false;
                foreach (var asset in mode.Value)
                    if (!names.TryGetValue(asset.Key, out string id) || id != asset.Value) return false;
            }
            return true;
        }

        private static void Fail(Exception ex)
        {
            s_Error = ex.Message;
            Mod.log.Info("Broadcast asset operation failed: " + s_Error);
        }

        private static void InvalidIndex(string error)
        {
            s_Index = null;
            s_Error = error;
            Mod.log.Info(error);
        }

        [DataContract]
        internal sealed class ReferenceIndex
        {
            [DataMember] public int version = 1;
            [DataMember] public Dictionary<string, FileRecord> files = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
            [DataMember] public Dictionary<string, Dictionary<string, Dictionary<string, string>>> saves =
                new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        }

        [DataContract]
        internal sealed class FileRecord
        {
            [DataMember] public List<string> sources = new List<string>();
            [DataMember] public bool allowAutoDelete;
        }

        internal static string Import(string source, out string assetId, out string error)
            => Import(source, out assetId, out error, out _);

        internal static string Import(string source, out string assetId, out string error, out bool created)
        {
            created = false;
            assetId = string.Empty;
            error = string.Empty;
            string root = AssetScope.RootDir();
            string temporary = IoPath.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string extension = IoPath.GetExtension(source).ToLowerInvariant();
                if (extension != ".wav" && extension != ".mp3" && extension != ".ogg")
                {
                    error = "Unsupported broadcast audio format.";
                    return string.Empty;
                }
                byte[] buffer = new byte[81920];
                using (FileStream input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (SHA256 hash = SHA256.Create())
                {
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        output.Write(buffer, 0, count);
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    assetId = BitConverter.ToString(hash.Hash).Replace("-", string.Empty).ToLowerInvariant() + extension;
                }

                string destination = IoPath.Combine(root, assetId);
                if (File.Exists(destination))
                {
                    if (!MatchesHash(destination, assetId.Substring(0, 64)))
                    {
                        error = "Existing broadcast audio content does not match its identity.";
                        assetId = string.Empty;
                        return string.Empty;
                    }
                }
                else
                {
                    File.Move(temporary, destination);
                    created = true;
                }
                return destination;
            }
            catch (Exception ex)
            {
                assetId = string.Empty;
                error = ex.Message;
                return string.Empty;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { Mod.log.Info("Broadcast temporary file cleanup failed: " + ex.Message); }
            }
        }

        internal static string Resolve(string assetId)
        {
            if (!ValidId(assetId)) return string.Empty;
            string path = IoPath.Combine(AssetScope.RootDir(), assetId);
            return File.Exists(path) ? path : string.Empty;
        }

        private static bool ValidId(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || assetId.Length < 68)
                return false;
            for (int i = 0; i < 64; i++)
            {
                char c = assetId[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            string extension = assetId.Substring(64);
            if (extension != ".wav" && extension != ".mp3" && extension != ".ogg")
                return false;
            return true;
        }

        private static bool MatchesHash(string path, string expected)
        {
            using FileStream input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 hash = SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        internal static bool HasContent(string path, string id, out string error)
        {
            error = string.Empty;
            try { return MatchesHash(path, id.Substring(0, 64)); }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
