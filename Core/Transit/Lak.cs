using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    /// <summary>
    /// Line Anchor Key: persistent per-line identity (32-char lowercase hex GUID only).
    /// Stable business key is built as mode + ":" + Value (e.g. train:0f4a...), not stored here.
    /// </summary>
    public struct Lak : IComponentData, ISerializable
    {
        public FixedString64Bytes Value;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Value.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string value);
            Value = value ?? string.Empty;
        }
    }
}
