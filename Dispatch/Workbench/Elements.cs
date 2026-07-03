using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RapidTransitMod
{
    [InternalBufferCapacity(32)]
    public struct AppliedWorkbenchLineStateElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public int m_OriginHoldLimitMinutes;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_OriginHoldLimitMinutes);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_OriginHoldLimitMinutes);
        }
    }

    [InternalBufferCapacity(1)]
    public struct AppliedWorkbenchStagedRowElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public int m_Order;
        public int m_Minute;
        public byte m_KindCode;
        public byte m_SourceCode;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_Order);
            writer.Write(m_Minute);
            writer.Write(m_KindCode);
            writer.Write(m_SourceCode);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_Order);
            reader.Read(out m_Minute);
            reader.Read(out m_KindCode);
            reader.Read(out m_SourceCode);
        }
    }
}
