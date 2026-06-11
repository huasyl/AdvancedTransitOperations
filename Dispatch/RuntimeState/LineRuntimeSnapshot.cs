using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct LineRuntimeSnapshot
    {
        public readonly Entity Line;
        public readonly bool Managed;
        public readonly bool Local;
        public readonly bool Express;
        public readonly int Dwell;
        public readonly LineFrame Frame;

        public LineRuntimeSnapshot(
            Entity line,
            bool managed,
            bool local,
            bool express,
            int dwell,
            LineFrame frame)
        {
            Line = line;
            Managed = managed;
            Local = local;
            Express = express;
            Dwell = dwell;
            Frame = frame;
        }
    }
}
