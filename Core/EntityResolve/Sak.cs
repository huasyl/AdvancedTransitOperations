using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public struct Sak : IComponentData, ISerializable
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
