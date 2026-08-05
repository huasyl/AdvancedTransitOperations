using Unity.Entities;

namespace RapidTransitMod.Runtime
{
    internal readonly struct RetireCommand
    {
        public readonly Entity Vehicle;
        public RetireCommand(Entity vehicle) { Vehicle = vehicle; }
    }

    internal readonly struct RecheckCommand
    {
        public readonly Entity Vehicle;
        public RecheckCommand(Entity vehicle) { Vehicle = vehicle; }
    }

    internal readonly struct DepartCommand
    {
        public readonly Entity Vehicle;
        public DepartCommand(Entity vehicle) { Vehicle = vehicle; }
    }

    internal readonly struct SpawnCommand
    {
        public readonly Entity Line;
        public SpawnCommand(Entity line) { Line = line; }
    }

    internal enum UiCommandKind : byte
    {
        Retire,
        Recheck,
        Depart,
        Spawn
    }

    internal readonly struct UiCommand
    {
        public readonly UiCommandKind Kind;
        public readonly Entity Entity;

        public UiCommand(UiCommandKind kind, Entity entity)
        {
            Kind = kind;
            Entity = entity;
        }
    }
}
