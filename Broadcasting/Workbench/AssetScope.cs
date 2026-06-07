using System;
using System.Collections.Generic;
using System.IO;
using IoPath = System.IO.Path;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal readonly struct AssetScope
    {
        private readonly ModeScope m_Mode;

        internal AssetScope(ModeScope mode)
        {
            m_Mode = mode;
        }

        internal ModeScope Mode => m_Mode;
        internal string Token => m_Mode.Token;

        internal string EnsureDir()
        {
            string root = RootDir();
            if (string.IsNullOrEmpty(root))
                return string.Empty;

            Directory.CreateDirectory(root);
            string scoped = IoPath.Combine(root, Token);
            if (Token == ModeScope.DefaultWorkbench.Token)
            {
                MigrateLegacyRoot(root, scoped);
            }

            Directory.CreateDirectory(scoped);
            return Assets.NormalizeDirectoryBrowserPath(scoped);
        }

        internal static string RootDir()
        {
            string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLowPath = IoPath.Combine(Directory.GetParent(localAppDataPath).FullName, "LocalLow");
            return IoPath.Combine(localLowPath, "Colossal Order", "Cities Skylines II", "ModsData", Mod.Id, "BroadcastAssets");
        }

        private static void MigrateLegacyRoot(string root, string trainDir)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(trainDir) || !Directory.Exists(root))
                return;

            List<string> legacyFiles = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsAudioAsset(file))
                {
                    legacyFiles.Add(file);
                }
            }

            if (legacyFiles.Count == 0)
                return;

            Directory.CreateDirectory(trainDir);
            foreach (string file in legacyFiles)
            {
                string destination = IoPath.Combine(trainDir, IoPath.GetFileName(file));
                if (File.Exists(destination))
                    throw new InvalidOperationException("Cannot migrate legacy broadcast asset; target already exists: " + IoPath.GetFileName(file));
            }

            List<Tuple<string, string>> movedFiles = new List<Tuple<string, string>>();
            try
            {
                foreach (string file in legacyFiles)
                {
                    string destination = IoPath.Combine(trainDir, IoPath.GetFileName(file));
                    File.Move(file, destination);
                    movedFiles.Add(Tuple.Create(file, destination));
                }
            }
            catch (Exception ex)
            {
                for (int i = movedFiles.Count - 1; i >= 0; i--)
                {
                    string source = movedFiles[i].Item1;
                    string destination = movedFiles[i].Item2;
                    try
                    {
                        if (!File.Exists(source) && File.Exists(destination))
                        {
                            File.Move(destination, source);
                        }
                    }
                    catch
                    {
                    }
                }

                throw new InvalidOperationException("Legacy broadcast asset migration failed; original files were preserved where possible.", ex);
            }
        }

        private static bool IsAudioAsset(string file)
        {
            string extension = IoPath.GetExtension(file);
            return string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase);
        }
    }
}
