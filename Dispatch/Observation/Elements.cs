using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    [InternalBufferCapacity(1)]
    public struct TraversalSliceQuotaElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public FixedString64Bytes m_LineKey;
        public int m_DateKey;
        public int m_UsedCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_LineKey.ToString());
            writer.Write(m_DateKey);
            writer.Write(m_UsedCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out string lineKey);
            m_LineKey = lineKey ?? string.Empty;
            reader.Read(out m_DateKey);
            reader.Read(out m_UsedCount);
        }
    }

    [InternalBufferCapacity(1)]
    public struct TraversalSliceColdStartElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public FixedString64Bytes m_LineKey;
        public ulong m_ProfileSignature;
        public int m_Remaining;
        public int m_PendingFinalMinute;
        public int m_PendingFinalDateKey;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_LineKey.ToString());
            writer.Write(m_ProfileSignature);
            writer.Write(m_Remaining);
            if (m_Version >= 2)
            {
                writer.Write(m_PendingFinalMinute);
                writer.Write(m_PendingFinalDateKey);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out string lineKey);
            m_LineKey = lineKey ?? string.Empty;
            reader.Read(out m_ProfileSignature);
            reader.Read(out m_Remaining);
            if (m_Version >= 2)
            {
                reader.Read(out m_PendingFinalMinute);
                reader.Read(out m_PendingFinalDateKey);
            }
            else
            {
                m_PendingFinalMinute = -1;
                m_PendingFinalDateKey = 0;
            }
        }
    }

    [InternalBufferCapacity(1)]
    public struct TraversalSliceObservationElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public ulong m_ProfileSignature;
        public int m_SliceIndex;
        public float m_AverageFrames;
        public float m_FastBaselineFrames;
        public int m_SampleCount;
        public uint m_LastObservedFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_ProfileSignature);
            writer.Write(m_SliceIndex);
            writer.Write(m_AverageFrames);
            writer.Write(m_FastBaselineFrames);
            writer.Write(m_SampleCount);
            writer.Write(m_LastObservedFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_ProfileSignature);
            reader.Read(out m_SliceIndex);
            reader.Read(out m_AverageFrames);
            reader.Read(out m_FastBaselineFrames);
            reader.Read(out m_SampleCount);
            reader.Read(out m_LastObservedFrame);
        }
    }

    [InternalBufferCapacity(1)]
    public struct DwellObservationElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public ulong m_ProfileSignature;
        public int m_WaypointIndex;
        public float m_AverageFrames;
        public int m_SampleCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_ProfileSignature);
            writer.Write(m_WaypointIndex);
            writer.Write(m_AverageFrames);
            writer.Write(m_SampleCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_ProfileSignature);
            reader.Read(out m_WaypointIndex);
            reader.Read(out m_AverageFrames);
            reader.Read(out m_SampleCount);
        }
    }

    [InternalBufferCapacity(1)]
    public struct StationDwellObservationElement : IBufferElementData, ISerializable
    {
        public FixedString64Bytes m_StationAnchorId;
        public float m_AverageFrames;
        public int m_SampleCount;
        public uint m_LastObservedFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_StationAnchorId.ToString());
            writer.Write(m_AverageFrames);
            writer.Write(m_SampleCount);
            writer.Write(m_LastObservedFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string stationAnchorId);
            m_StationAnchorId = stationAnchorId ?? string.Empty;
            reader.Read(out m_AverageFrames);
            reader.Read(out m_SampleCount);
            reader.Read(out m_LastObservedFrame);
        }
    }
}
