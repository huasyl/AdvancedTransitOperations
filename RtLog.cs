namespace RapidTransitMod
{
    internal static class RtLog
    {
        internal static bool DebugToolsEnabled => BuildFlavor.DebugTools;
        internal static bool VerboseEnabled => BuildFlavor.VerboseLogs;
        // Raw path dumps are much heavier than ordinary verbose diagnostics.
        internal static bool TrackRawDiagnosticsEnabled => VerboseEnabled && false;
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
#if RT_VERBOSE_LOGS
            Mod.log.Info(message);
#endif
        }

        internal static void Verbose(string message)
        {
#if RT_VERBOSE_LOGS
            Mod.log.Info(message);
#endif
        }
    }
}
