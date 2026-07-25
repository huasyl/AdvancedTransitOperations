using System;
using Unity.Entities;

namespace RapidTransitMod.Runtime
{
    internal enum DeadlineKind : byte
    {
        Dwell,
        Ready,
        Idle,
        LaunchCooldown,
        PreparingCooldown,
        OriginBoardingGrace,
        ForcedMidStopBoardingGrace,
        OriginSettle,
        RetireBoundary,
        RetireHardAck,
        RescueProbe,
        RescueStall,
        RescueRecheck
    }

    internal readonly struct DeadlineKey : IEquatable<DeadlineKey>
    {
        public readonly Entity Vehicle;
        public readonly DeadlineKind Kind;

        public DeadlineKey(Entity vehicle, DeadlineKind kind)
        {
            Vehicle = vehicle;
            Kind = kind;
        }

        public bool Equals(DeadlineKey other) => Vehicle == other.Vehicle && Kind == other.Kind;
        public override bool Equals(object obj) => obj is DeadlineKey other && Equals(other);
        public override int GetHashCode() => (Vehicle.GetHashCode() * 397) ^ (int)Kind;
    }

    internal readonly struct DeadlineEntry
    {
        public readonly Entity Vehicle;
        public readonly DeadlineKind Kind;
        public readonly uint DueFrame;

        public DeadlineEntry(Entity vehicle, DeadlineKind kind, uint dueFrame)
        {
            Vehicle = vehicle;
            Kind = kind;
            DueFrame = dueFrame;
        }
    }
}
