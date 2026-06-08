namespace RapidTransitMod.PassengerFlow
{
    internal static class Runtime
    {
        public static Port Current { get; private set; }

        public static void Bind(Port port)
        {
            Current = port;
        }

        public static void Clear()
        {
            Current = null;
        }
    }
}
