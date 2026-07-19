using Unity.Entities;

namespace RapidTransitMod
{
    public static class LineIdentityService
    {
        public static LineKey GetKey(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return LineKey.Empty;

            if (LineKey.TryParse(lineId, out LineKey key))
                return key;

            return new LineKey(TransitMode.Unknown, lineId);
        }

        public static LineKey GetKey(string lineId, TransitMode mode)
        {
            if (LineKey.TryParse(lineId, mode, out LineKey key))
                return key;

            return mode == TransitMode.Unknown
                ? GetKey(lineId)
                : LineKey.Empty;
        }

        /// <summary>Stable identity from LineAnchorCatalog (mode:guid32). Getter only; never writes.</summary>
        internal static LineKey StableKey(LineAnchorCatalog catalog, Entity line)
        {
            if (catalog == null || line == Entity.Null)
                return LineKey.Empty;

            return catalog.StableKey(line);
        }

        internal static string StableId(LineAnchorCatalog catalog, Entity line)
        {
            return GetId(StableKey(catalog, line));
        }

        public static string GetId(LineKey lineKey)
        {
            if (lineKey.IsEmpty)
                return string.Empty;

            return lineKey.Mode == TransitMode.Unknown
                ? lineKey.GetLegacyId()
                : lineKey.ToString();
        }

        public static bool TryGetMode(string lineId, out TransitMode mode)
        {
            return GetKey(lineId).TryGetMode(out mode);
        }

        public static string WithMode(string lineId, TransitMode mode)
        {
            return GetId(GetKey(lineId, mode));
        }

        public static string NormalizeForMode(string lineId, TransitMode mode)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            return GetId(GetKey(lineId).NormalizeForMode(mode));
        }

        public static string GetLegacyId(string lineId)
        {
            return GetKey(lineId).GetLegacyId();
        }

        public static string GetLegacyId(LineKey lineKey)
        {
            return lineKey.GetLegacyId();
        }
    }
}
