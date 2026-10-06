using System;
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
            Directory.CreateDirectory(scoped);
            return Assets.NormalizeDirectoryBrowserPath(scoped);
        }

        internal static string RootDir()
        {
            string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLowPath = IoPath.Combine(Directory.GetParent(localAppDataPath).FullName, "LocalLow");
            return IoPath.Combine(localLowPath, "Colossal Order", "Cities Skylines II", "ModsData", Mod.Id, "BroadcastAssets");
        }

    }
}
