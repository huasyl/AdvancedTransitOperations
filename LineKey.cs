using System;

namespace RapidTransitMod
{
    public readonly struct LineKey : IEquatable<LineKey>
    {
        public static readonly LineKey Empty = new LineKey(TransitMode.Unknown, string.Empty);

        public LineKey(TransitMode mode, string id)
        {
            Mode = mode;
            Id = id ?? string.Empty;
        }

        public TransitMode Mode { get; }
        public string Id { get; }
        public bool IsEmpty => string.IsNullOrEmpty(Id);
        public bool HasMode => !IsEmpty && Mode != TransitMode.Unknown;

        public bool TryGetMode(out TransitMode mode)
        {
            mode = Mode;
            return HasMode;
        }

        public string GetLegacyId()
        {
            return Id ?? string.Empty;
        }

        public LineKey WithMode(TransitMode mode)
        {
            if (IsEmpty)
                return Empty;

            return new LineKey(mode, GetLegacyId());
        }

        public LineKey NormalizeForMode(TransitMode mode)
        {
            if (IsEmpty || mode == TransitMode.Unknown || Mode != TransitMode.Unknown)
                return this;

            return new LineKey(mode, GetLegacyId());
        }

        public override string ToString()
        {
            if (IsEmpty)
                return string.Empty;

            return TransitModeCodec.Format(Mode) + ":" + Id;
        }

        public override bool Equals(object obj)
        {
            return obj is LineKey other && Equals(other);
        }

        public bool Equals(LineKey other)
        {
            return Mode == other.Mode
                && string.Equals(Id, other.Id, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ (Id != null ? StringComparer.Ordinal.GetHashCode(Id) : 0);
            }
        }

        public static LineKey Parse(string value)
        {
            if (!TryParse(value, out LineKey key))
                throw new FormatException("Invalid line key: " + (value ?? string.Empty));

            return key;
        }

        public static bool TryParse(string value, out LineKey key)
        {
            key = Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int separator = value.IndexOf(':');
            if (separator <= 0 || separator >= value.Length - 1)
                return false;

            string modeToken = value.Substring(0, separator);
            string id = value.Substring(separator + 1);
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (!TransitModeCodec.TryParse(modeToken, out TransitMode mode))
                return false;

            key = new LineKey(mode, id);
            return true;
        }

        public static bool TryParse(string value, TransitMode mode, out LineKey key)
        {
            if (TryParse(value, out key))
                return true;

            key = Empty;
            if (mode == TransitMode.Unknown
                || string.IsNullOrWhiteSpace(value)
                || value.IndexOf(':') >= 0)
            {
                return false;
            }

            key = new LineKey(mode, value);
            return true;
        }

        public static bool operator ==(LineKey left, LineKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(LineKey left, LineKey right)
        {
            return !left.Equals(right);
        }
    }
}
