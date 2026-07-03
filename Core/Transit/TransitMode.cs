namespace RapidTransitMod
{
    public enum TransitMode
    {
        Unknown = 0,
        Train = 1,
        Subway = 2,
        Tram = 3,
        Bus = 4
    }

    public enum LifecycleKind
    {
        Unknown = 0,
        Rail = 1,
        Road = 2
    }

    internal static class TransitModeCodec
    {
        public static string Format(TransitMode mode)
        {
            switch (mode)
            {
                case TransitMode.Train:
                    return "train";
                case TransitMode.Subway:
                    return "subway";
                case TransitMode.Tram:
                    return "tram";
                case TransitMode.Bus:
                    return "bus";
                default:
                    return "unknown";
            }
        }

        public static bool TryParse(string value, out TransitMode mode)
        {
            mode = TransitMode.Unknown;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "train":
                    mode = TransitMode.Train;
                    return true;
                case "subway":
                    mode = TransitMode.Subway;
                    return true;
                case "tram":
                    mode = TransitMode.Tram;
                    return true;
                case "bus":
                    mode = TransitMode.Bus;
                    return true;
                case "unknown":
                    mode = TransitMode.Unknown;
                    return true;
                default:
                    return false;
            }
        }
    }
}
