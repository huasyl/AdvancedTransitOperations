using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RapidTransitMod
{
    [InternalBufferCapacity(32)]
    public struct LineMileageModelStateElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public uint m_TotalDistanceMeters;
        public int m_WaypointCount;
        public ulong m_Signature;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_TotalDistanceMeters);
            writer.Write(m_WaypointCount);
            writer.Write(m_Signature);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_TotalDistanceMeters);
            reader.Read(out m_WaypointCount);
            reader.Read(out m_Signature);
        }
    }

    [InternalBufferCapacity(1)]
    public struct LineMileageAnchorElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public int m_WaypointIndex;
        public uint m_CumulativeDistanceMeters;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_WaypointIndex);
            writer.Write(m_CumulativeDistanceMeters);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_WaypointIndex);
            reader.Read(out m_CumulativeDistanceMeters);
        }
    }

    [InternalBufferCapacity(32)]
    public struct LineCorridorStateElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public ulong m_Signature;
        public uint m_TotalDistanceMeters;
        public int m_NodeCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_Signature);
            writer.Write(m_TotalDistanceMeters);
            writer.Write(m_NodeCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_Signature);
            reader.Read(out m_TotalDistanceMeters);
            reader.Read(out m_NodeCount);
        }
    }

    [InternalBufferCapacity(1)]
    public struct LineCorridorNodeElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public Entity m_BuildingEntity;
        public uint m_DistanceMeters;
        public byte m_IsStopNode;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_BuildingEntity);
            writer.Write(m_DistanceMeters);
            writer.Write(m_IsStopNode);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_BuildingEntity);
            reader.Read(out m_DistanceMeters);
            reader.Read(out m_IsStopNode);
        }
    }
}
