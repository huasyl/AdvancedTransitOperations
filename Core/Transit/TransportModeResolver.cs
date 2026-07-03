using System;
using Game.Prefabs;
using Unity.Entities;

namespace RapidTransitMod
{
    public static class TransportModeResolver
    {
        public static bool TryResolve(string value, out TransitMode mode)
        {
            mode = TransitMode.Unknown;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (TransitModeCodec.TryParse(value, out mode))
                return true;

            switch (value.Trim())
            {
                case "Train":
                    mode = TransitMode.Train;
                    return true;
                case "Subway":
                    mode = TransitMode.Subway;
                    return true;
                case "Tram":
                    mode = TransitMode.Tram;
                    return true;
                case "Bus":
                    mode = TransitMode.Bus;
                    return true;
                default:
                    return false;
            }
        }

        public static TransitMode Resolve(string value)
        {
            return TryResolve(value, out TransitMode mode)
                ? mode
                : TransitMode.Unknown;
        }

        public static TransitMode Resolve(TransportType transportType)
        {
            switch (transportType)
            {
                case TransportType.Train:
                    return TransitMode.Train;
                case TransportType.Subway:
                    return TransitMode.Subway;
                case TransportType.Tram:
                    return TransitMode.Tram;
                case TransportType.Bus:
                    return TransitMode.Bus;
                default:
                    return TransitMode.Unknown;
            }
        }

        public static TransitMode Resolve(TransportLineData lineData)
        {
            return Resolve(lineData.m_TransportType);
        }

        public static TransitMode Resolve(TransportDepotData depotData)
        {
            return Resolve(depotData.m_TransportType);
        }

        public static TransitMode Resolve(EntityManager entityManager, Entity line)
        {
            if (line == Entity.Null || !entityManager.Exists(line))
                return TransitMode.Unknown;

            if (entityManager.HasComponent<TransportLineData>(line))
                return Resolve(entityManager.GetComponentData<TransportLineData>(line));

            if (!entityManager.HasComponent<PrefabRef>(line))
                return TransitMode.Unknown;

            Entity prefab = entityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !entityManager.HasComponent<TransportLineData>(prefab))
                return TransitMode.Unknown;

            return Resolve(entityManager.GetComponentData<TransportLineData>(prefab));
        }

        public static TransportModeProfile GetProfile(string value)
        {
            return TransportModeProfile.GetProfile(Resolve(value));
        }

        public static TransportModeProfile GetProfile(EntityManager entityManager, Entity line)
        {
            return TransportModeProfile.GetProfile(Resolve(entityManager, line));
        }
    }
}
