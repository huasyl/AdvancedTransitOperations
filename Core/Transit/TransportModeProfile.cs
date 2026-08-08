using System;

namespace RapidTransitMod
{
    public readonly struct TransportModeProfile : IEquatable<TransportModeProfile>
    {
        public TransportModeProfile(
            TransitMode mode,
            bool canDispatch,
            bool canBypass,
            LifecycleKind lifecycle)
        {
            Mode = mode;
            CanDispatch = canDispatch;
            CanBypass = canBypass;
            Lifecycle = lifecycle;
        }

        public TransitMode Mode { get; }
        public bool CanDispatch { get; }
        public bool CanBypass { get; }
        public LifecycleKind Lifecycle { get; }
        public bool IsKnown => Mode != TransitMode.Unknown;
        public bool IsSupported => CanDispatch;
        public bool IsReserved => IsKnown && !IsSupported;
        public string Token => TransitModeCodec.Format(Mode);

        public static TransportModeProfile GetProfile(TransitMode mode)
        {
            switch (mode)
            {
                case TransitMode.Train:
                case TransitMode.Subway:
                    return new TransportModeProfile(mode, canDispatch: true, canBypass: true, lifecycle: LifecycleKind.Rail);
                case TransitMode.Bus:
                    return new TransportModeProfile(mode, canDispatch: true, canBypass: false, lifecycle: LifecycleKind.Road);
                case TransitMode.Tram:
                    return new TransportModeProfile(mode, canDispatch: true, canBypass: false, lifecycle: LifecycleKind.Rail);
                default:
                    return new TransportModeProfile(TransitMode.Unknown, canDispatch: false, canBypass: false, lifecycle: LifecycleKind.Unknown);
            }
        }

        public static TransportModeProfile GetProfile(LineKey lineKey)
        {
            return GetProfile(lineKey.Mode);
        }

        public static TransportModeProfile GetProfile(string lineId)
        {
            return GetProfile(LineIdentityService.GetKey(lineId));
        }

        public override bool Equals(object obj)
        {
            return obj is TransportModeProfile other && Equals(other);
        }

        public bool Equals(TransportModeProfile other)
        {
            return Mode == other.Mode
                && CanDispatch == other.CanDispatch
                && CanBypass == other.CanBypass
                && Lifecycle == other.Lifecycle;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ CanDispatch.GetHashCode();
                hash = (hash * 397) ^ CanBypass.GetHashCode();
                hash = (hash * 397) ^ (int)Lifecycle;
                return hash;
            }
        }

        public static bool operator ==(TransportModeProfile left, TransportModeProfile right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TransportModeProfile left, TransportModeProfile right)
        {
            return !left.Equals(right);
        }
    }
}
