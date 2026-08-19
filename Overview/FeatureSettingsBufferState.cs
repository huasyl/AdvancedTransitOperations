using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    [InternalBufferCapacity(0)]
    public struct OverviewFeatureSettingsStateElement : IBufferElementData, ISerializable
    {
        public int m_ChunkIndex;
        public FixedString4096Bytes m_PayloadChunk;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_ChunkIndex);
            writer.Write(m_PayloadChunk.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_ChunkIndex);
            reader.Read(out string payload);
            m_PayloadChunk = payload ?? string.Empty;
        }
    }
}
