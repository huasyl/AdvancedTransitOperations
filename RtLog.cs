namespace RapidTransitMod
{
    internal static class RtLog
    {
        internal static bool DebugToolsEnabled => BuildFlavor.DebugTools;
        internal static bool VerboseEnabled => Mod.Options?.EnableVerboseLogging == true;
        internal static bool CacheInvalidationDiagnosticsEnabled => VerboseEnabled;

        internal static void Info(string message)
        {
            Mod.log.Info(message);
        }

        internal static void Warn(string message)
        {
            Mod.log.Info("[warn] " + message);
        }

        internal static void Error(string message)
        {
            Mod.log.Info("[error] " + message);
        }

        internal static void Diagnostics(string message)
        {
            if (VerboseEnabled)
                Mod.log.Info(message);
        }

        internal static void Verbose(string message)
        {
            if (VerboseEnabled)
                Mod.log.Info(message);
        }
    }
}
