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

    [InternalBufferCapacity(32)]
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

    [InternalBufferCapacity(32)]
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

    // This buffer is persisted on the city singleton and may be added to old
    // saves during load. Keep the in-chunk footprint tiny so the city
    // archetype does not bloat when the buffer is first attached.
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

    // This buffer lives on the city singleton and can grow large in old saves.
    // Keep the in-chunk footprint tiny so adding the buffer does not bloat the
    // city archetype during load; the dynamic buffer can spill externally.
    [InternalBufferCapacity(1)]
    public struct StopDwellObservationElement : IBufferElementData, ISerializable
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
    public struct StationStopDwellObservationElement : IBufferElementData, ISerializable
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

    // This persisted buffer is attached to the city singleton. Keep the
    // in-chunk footprint minimal so old saves can attach it safely on first load.
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

    [InternalBufferCapacity(32)]
    public struct BypassStationSettingElement : IBufferElementData, ISerializable
    {
        public Entity m_BuildingEntity;
        public byte m_IsBypassStation;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_BuildingEntity);
            writer.Write(m_IsBypassStation);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_BuildingEntity);
            reader.Read(out m_IsBypassStation);
        }
    }

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

    // This persisted buffer is attached to the city singleton. Keep the
    // in-chunk footprint minimal so old saves can attach it safely on first load.
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

    // This persisted buffer is attached to the city singleton. Keep the
    // in-chunk footprint minimal so old saves can attach it safely on first load.
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
