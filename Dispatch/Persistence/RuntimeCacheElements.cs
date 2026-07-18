using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    [InternalBufferCapacity(64)]
    public struct VehicleStateCacheElement : IBufferElementData, ISerializable
    {
        public Entity m_VehicleEntity;
        public VehicleState m_State;
        public int m_TargetMin;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_VehicleEntity);
            writer.Write((int)m_State);
            writer.Write(m_TargetMin);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_VehicleEntity);
            reader.Read(out int state);
            m_State = (VehicleState)state;
            reader.Read(out m_TargetMin);
        }
    }

    [InternalBufferCapacity(32)]
    public struct LineLapCacheElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public uint m_MaxLapFrames;
        public float m_MaxLapDistance;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_MaxLapFrames);
            writer.Write(m_MaxLapDistance);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_MaxLapFrames);
            reader.Read(out m_MaxLapDistance);
        }
    }

    [InternalBufferCapacity(32)]
    public struct LineDispatchCacheElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public uint m_DepotToOriginFrames;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_DepotToOriginFrames);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_DepotToOriginFrames);
        }
    }

    [InternalBufferCapacity(32)]
    public struct LineDispatchHistoryElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public byte m_SampleCount;
        public uint m_Sample0;
        public uint m_Sample1;
        public uint m_Sample2;
        public uint m_Sample3;
        public uint m_Sample4;
        public uint m_Sample5;
        public uint m_Sample6;
        public uint m_Sample7;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_SampleCount);
            writer.Write(m_Sample0);
            writer.Write(m_Sample1);
            writer.Write(m_Sample2);
            writer.Write(m_Sample3);
            writer.Write(m_Sample4);
            writer.Write(m_Sample5);
            writer.Write(m_Sample6);
            writer.Write(m_Sample7);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_SampleCount);
            reader.Read(out m_Sample0);
            reader.Read(out m_Sample1);
            reader.Read(out m_Sample2);
            reader.Read(out m_Sample3);
            reader.Read(out m_Sample4);
            reader.Read(out m_Sample5);
            reader.Read(out m_Sample6);
            reader.Read(out m_Sample7);
        }
    }

    [InternalBufferCapacity(1)]
    public struct LineDispatchDepotCacheElement : IBufferElementData, ISerializable
    {
        public FixedString128Bytes m_LineId;
        public FixedString128Bytes m_DepotId;
        public uint m_DepotToOriginFrames;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineId.ToString());
            writer.Write(m_DepotId.ToString());
            writer.Write(m_DepotToOriginFrames);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string lineId);
            reader.Read(out string depotId);
            reader.Read(out m_DepotToOriginFrames);
            m_LineId = lineId ?? string.Empty;
            m_DepotId = depotId ?? string.Empty;
        }
    }

    [InternalBufferCapacity(1)]
    public struct LineDispatchDepotHistoryElement : IBufferElementData, ISerializable
    {
        public FixedString128Bytes m_LineId;
        public FixedString128Bytes m_DepotId;
        public byte m_SampleCount;
        public uint m_Sample0;
        public uint m_Sample1;
        public uint m_Sample2;
        public uint m_Sample3;
        public uint m_Sample4;
        public uint m_Sample5;
        public uint m_Sample6;
        public uint m_Sample7;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineId.ToString());
            writer.Write(m_DepotId.ToString());
            writer.Write(m_SampleCount);
            writer.Write(m_Sample0);
            writer.Write(m_Sample1);
            writer.Write(m_Sample2);
            writer.Write(m_Sample3);
            writer.Write(m_Sample4);
            writer.Write(m_Sample5);
            writer.Write(m_Sample6);
            writer.Write(m_Sample7);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string lineId);
            reader.Read(out string depotId);
            reader.Read(out m_SampleCount);
            reader.Read(out m_Sample0);
            reader.Read(out m_Sample1);
            reader.Read(out m_Sample2);
            reader.Read(out m_Sample3);
            reader.Read(out m_Sample4);
            reader.Read(out m_Sample5);
            reader.Read(out m_Sample6);
            reader.Read(out m_Sample7);
            m_LineId = lineId ?? string.Empty;
            m_DepotId = depotId ?? string.Empty;
        }
    }

    [InternalBufferCapacity(1)]
    public struct LineDispatchPrepHistoryElement : IBufferElementData, ISerializable
    {
        public FixedString128Bytes m_LineId;
        public FixedString128Bytes m_DepotId;
        public byte m_SampleCount;
        public uint m_Sample0;
        public uint m_Sample1;
        public uint m_Sample2;
        public uint m_Sample3;
        public uint m_Sample4;
        public uint m_Sample5;
        public uint m_Sample6;
        public uint m_Sample7;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineId.ToString());
            writer.Write(m_DepotId.ToString());
            writer.Write(m_SampleCount);
            writer.Write(m_Sample0);
            writer.Write(m_Sample1);
            writer.Write(m_Sample2);
            writer.Write(m_Sample3);
            writer.Write(m_Sample4);
            writer.Write(m_Sample5);
            writer.Write(m_Sample6);
            writer.Write(m_Sample7);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string lineId);
            reader.Read(out string depotId);
            reader.Read(out m_SampleCount);
            reader.Read(out m_Sample0);
            reader.Read(out m_Sample1);
            reader.Read(out m_Sample2);
            reader.Read(out m_Sample3);
            reader.Read(out m_Sample4);
            reader.Read(out m_Sample5);
            reader.Read(out m_Sample6);
            reader.Read(out m_Sample7);
            m_LineId = lineId ?? string.Empty;
            m_DepotId = depotId ?? string.Empty;
        }
    }
}
