namespace RapidTransitMod
{
    internal static class RtLog
    {
        internal static bool DebugToolsEnabled => BuildFlavor.DebugTools;
        internal static bool VerboseEnabled => BuildFlavor.VerboseLogs;
        internal static bool CacheInvalidationDiagnosticsEnabled => false;

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
