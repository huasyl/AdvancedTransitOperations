using System;
using System.Runtime.Serialization;

namespace RapidTransitMod.Workbenches
{
    internal static class ModeRequest
    {
        internal static ModeScope ReadScope(string requestJson, string apiName, bool allowLegacyDefault = false)
        {
            if (!TryReadJson(requestJson, out ModeRequestDto request))
            {
                if (allowLegacyDefault)
                    return ModeScope.DefaultWorkbench;

                if (RtLog.VerboseEnabled)
                    Mod.log.Info("[WorkbenchMode] " + (apiName ?? string.Empty) + " received legacy non-JSON request; defaulting to train.");
                return ModeScope.DefaultWorkbench;
            }

            string modeToken = request?.mode;
            if (string.IsNullOrWhiteSpace(modeToken))
            {
                if (RtLog.VerboseEnabled)
                    Mod.log.Info("[WorkbenchMode] " + (apiName ?? string.Empty) + " request is missing mode; defaulting to train.");
                return ModeScope.DefaultWorkbench;
            }

            if (!ModeScope.TryParseWorkbench(modeToken, out ModeScope scope))
                throw new InvalidOperationException("Invalid workbench mode: " + modeToken);

            scope.EnsureSupportedWorkbenchMode();
            return scope;
        }

        internal static string ReadPreferredLine(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.preferredLineId ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static string ReadLine(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.lineId ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static string ReadOperationId(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.operationId ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static string ReadPath(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.path ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static string ReadAssetName(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.assetName ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static string ReadRuleId(string requestJson)
        {
            return TryReadJson(requestJson, out ModeRequestDto request)
                ? request?.ruleId ?? string.Empty
                : requestJson ?? string.Empty;
        }

        internal static int ReadVolume(string requestJson, int fallback)
        {
            if (TryReadJson(requestJson, out ModeRequestDto request))
                return request?.volume ?? fallback;

            return int.TryParse(requestJson ?? string.Empty, out int parsed)
                ? parsed
                : fallback;
        }

        private static bool TryReadJson(string requestJson, out ModeRequestDto request)
        {
            request = null;
            string trimmed = requestJson?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                request = new ModeRequestDto();
                return true;
            }

            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                return false;

            request = Json.Read<ModeRequestDto>(trimmed);
            return true;
        }

        [DataContract]
        private sealed class ModeRequestDto
        {
            [DataMember]
            public string mode = string.Empty;
            [DataMember]
            public string preferredLineId = string.Empty;
            [DataMember]
            public string lineId = string.Empty;
            [DataMember]
            public string operationId = string.Empty;
            [DataMember]
            public string path = string.Empty;
            [DataMember]
            public string assetName = string.Empty;
            [DataMember]
            public string ruleId = string.Empty;
            [DataMember]
            public int? volume = null;
        }
    }
}
