namespace RapidTransitMod
{
    internal static class BuildFlavor
    {
#if RT_DEBUG_TOOLS
        internal const bool DebugTools = true;
#else
        internal const bool DebugTools = false;
#endif

#if RT_VERBOSE_LOGS
        internal const bool VerboseLogs = true;
#else
        internal const bool VerboseLogs = false;
#endif

#if RT_PERF_LOGS
        internal const bool PerfLogs = true;
#else
        internal const bool PerfLogs = false;
#endif
    }
}
