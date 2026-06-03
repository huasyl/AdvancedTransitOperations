using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
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
