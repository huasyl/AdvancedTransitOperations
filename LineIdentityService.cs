using System.Globalization;
using Game.Routes;
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

        public static LineKey GetKey(TransitMode mode, int routeNumber, Entity line)
        {
            if (routeNumber >= 0 && routeNumber != int.MaxValue)
                return new LineKey(mode, routeNumber.ToString(CultureInfo.InvariantCulture));

            // Entity.Index is only a runtime fallback when route numbering is unavailable.
            if (line != Entity.Null)
                return new LineKey(mode, "entity-" + line.Index.ToString(CultureInfo.InvariantCulture));

            return LineKey.Empty;
        }

        public static LineKey GetKey(EntityManager entityManager, Entity line)
        {
            if (line == Entity.Null || !entityManager.Exists(line))
                return LineKey.Empty;

            int routeNumber = int.MaxValue;
            if (entityManager.HasComponent<RouteNumber>(line))
                routeNumber = entityManager.GetComponentData<RouteNumber>(line).m_Number;

            TransitMode mode = TransportModeResolver.Resolve(entityManager, line);
            return GetKey(mode, routeNumber, line);
        }

        public static LineKey GetKey(EntityManager entityManager, Entity line, string fallbackLineId)
        {
            LineKey key = GetKey(entityManager, line);
            if (!key.IsEmpty)
                return key;

            return GetKey(fallbackLineId);
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
