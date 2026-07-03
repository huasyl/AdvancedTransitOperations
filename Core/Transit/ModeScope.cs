using System;
using System.Collections.Generic;

namespace RapidTransitMod
{
    internal readonly struct ModeScope
    {
        public static ModeScope DefaultWorkbench => new ModeScope(TransitMode.Train);

        public ModeScope(TransitMode mode)
        {
            Mode = mode;
            Token = TransitModeCodec.Format(mode);
        }

        public TransitMode Mode { get; }
        public string Token { get; }
        public bool IsSupportedWorkbenchMode => Mode == TransitMode.Train || Mode == TransitMode.Subway;

        public static bool TryParseWorkbench(string modeToken, out ModeScope scope)
        {
            scope = default;
            if (!TransitModeCodec.TryParse(modeToken, out TransitMode mode))
                return false;

            scope = new ModeScope(mode);
            return true;
        }

        public void EnsureSupportedWorkbenchMode()
        {
            if (!IsSupportedWorkbenchMode)
                throw new InvalidOperationException("Unsupported workbench mode: " + Token);
        }

        public bool MatchesLineId(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return true;

            if (LineKey.TryParse(lineId, out LineKey key))
                return key.Mode == Mode;

            return !lineId.Contains(":");
        }

        public string NormalizeLineId(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            return LineIdentityService.NormalizeForMode(lineId.Trim(), Mode);
        }

        public void ValidateLineId(string lineId, string fieldName, List<string> errors)
        {
            if (errors == null || string.IsNullOrWhiteSpace(lineId))
                return;

            if (!MatchesLineId(lineId))
            {
                errors.Add((string.IsNullOrEmpty(fieldName) ? "lineId" : fieldName)
                    + " does not belong to mode " + Token + ": " + lineId);
            }
        }
    }
}
