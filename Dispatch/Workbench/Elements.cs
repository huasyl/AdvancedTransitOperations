using Colossal.Serialization.Entities;
using Unity.Collections;
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

    [InternalBufferCapacity(0)]
    public struct AppliedRowIdElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public Entity m_LineEntity;
        public int m_Order;
        public FixedString128Bytes m_RowId;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_LineEntity);
            writer.Write(m_Order);
            writer.Write(m_RowId.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_LineEntity);
            reader.Read(out m_Order);
            reader.Read(out string rowId);
            m_RowId = rowId ?? string.Empty;
        }
    }

    [InternalBufferCapacity(0)]
    public struct AppliedStopSigElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public Entity m_LineEntity;
        public FixedString64Bytes m_StopSig;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_LineEntity);
            writer.Write(m_StopSig.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_LineEntity);
            reader.Read(out string stopSig);
            m_StopSig = stopSig ?? string.Empty;
        }
    }

    [InternalBufferCapacity(0)]
    public struct AppliedTimedStopElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public Entity m_LineEntity;
        public int m_RowOrder;
        public int m_StopOrder;
        public FixedString64Bytes m_StopKey;
        public int m_Arrive;
        public int m_Depart;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_LineEntity);
            writer.Write(m_RowOrder);
            writer.Write(m_StopOrder);
            writer.Write(m_StopKey.ToString());
            writer.Write(m_Arrive);
            writer.Write(m_Depart);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_LineEntity);
            reader.Read(out m_RowOrder);
            reader.Read(out m_StopOrder);
            reader.Read(out string stopKey);
            m_StopKey = stopKey ?? string.Empty;
            reader.Read(out m_Arrive);
            reader.Read(out m_Depart);
        }
    }
}
