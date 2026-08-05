using System;

namespace RapidTransitMod.Overview
{
    internal static class FeatureSettingsApi
    {
        internal static string Start(string requestJson)
        {
            return global::RapidTransitMod.ModRuntimeHostSystem.Instance?.m_OverviewFeatureSettingsOperations?.Start(requestJson) ?? string.Empty;
        }

        internal static string Status(string operationId)
        {
            return global::RapidTransitMod.ModRuntimeHostSystem.Instance?.m_OverviewFeatureSettingsOperations?.Status(operationId) ?? string.Empty;
        }
    }
}
