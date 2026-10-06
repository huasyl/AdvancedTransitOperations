using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    // 仅运行期代表测量，读档不恢复。
    public struct PassengerFlowTransferSample : IComponentData, IEnableableComponent
    {
        public Entity m_OldVehicle;
        public uint m_OpenFrame;
        public Entity m_TargetWaypoint;
        public Entity m_TargetStop;
        public int m_TargetMode;
        public FixedString128Bytes m_TargetLineId;
        public int m_TargetStationSakIndex;
        public int m_TargetStationOccurrence;
        public uint m_AlightFrame;
        public uint m_NextReadFrame;
        public uint m_WalkCheckIntervalFrames;
        public uint m_ExpiresFrame;
        public double m_WalkPathMeters;
        public byte m_HasWalkPath;
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowStateElement : IBufferElementData, ISerializable
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

    [InternalBufferCapacity(0)]
    public struct PassengerFlowVehicleElement : IBufferElementData, ISerializable
    {
        public Entity m_Vehicle;
        public FixedString128Bytes m_VehicleId;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Vehicle);
            writer.Write(m_VehicleId.ToString());
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Vehicle);
            reader.Read(out string vehicleId);
            m_VehicleId = vehicleId;
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowBaselineElement : IBufferElementData, ISerializable
    {
        public Entity m_Vehicle;
        public Entity m_Passenger;
        public byte m_IsEmpty;
        public int m_RawPurpose;
        public byte m_PurposeCategory;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Vehicle);
            writer.Write(m_Passenger);
            writer.Write(m_IsEmpty);
            writer.Write(m_RawPurpose);
            writer.Write(m_PurposeCategory);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Vehicle);
            reader.Read(out m_Passenger);
            reader.Read(out m_IsEmpty);
            reader.Read(out m_RawPurpose);
            reader.Read(out m_PurposeCategory);
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowSampleElement : IBufferElementData, ISerializable
    {
        public Entity m_Line;
        public Entity m_Vehicle;
        public Entity m_RuntimeVehicle;
        public FixedString128Bytes m_LineId;
        public FixedString128Bytes m_VehicleId;
        public uint m_DepartureFrame;
        public uint m_OpenFrame;
        public int m_DepartureDayIndex;
        public int m_DepartureBucketMinute;
        public uint m_RemainingSampleFrames;
        public int m_Mode;
        public int m_OpenWaypointIndex;
        public int m_OpenStationSakIndex;
        public Entity m_OpenStop;
        public Entity m_OpenWaypoint;
        public int m_StationOccurrence;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Line); writer.Write(m_Vehicle); writer.Write(m_RuntimeVehicle);
            writer.Write(m_LineId.ToString()); writer.Write(m_VehicleId.ToString());
            writer.Write(m_DepartureFrame); writer.Write(m_OpenFrame); writer.Write(m_DepartureDayIndex); writer.Write(m_DepartureBucketMinute);
            writer.Write(m_RemainingSampleFrames); writer.Write(m_Mode); writer.Write(m_OpenWaypointIndex);
            writer.Write(m_OpenStationSakIndex);
            writer.Write(m_OpenStop); writer.Write(m_OpenWaypoint); writer.Write(m_StationOccurrence);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Line); reader.Read(out m_Vehicle); reader.Read(out m_RuntimeVehicle);
            reader.Read(out string lineId); reader.Read(out string vehicleId);
            m_LineId = lineId; m_VehicleId = vehicleId;
            reader.Read(out m_DepartureFrame); reader.Read(out m_OpenFrame); reader.Read(out m_DepartureDayIndex); reader.Read(out m_DepartureBucketMinute);
            reader.Read(out m_RemainingSampleFrames); reader.Read(out m_Mode); reader.Read(out m_OpenWaypointIndex);
            reader.Read(out m_OpenStationSakIndex);
            reader.Read(out m_OpenStop); reader.Read(out m_OpenWaypoint); reader.Read(out m_StationOccurrence);
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowSectionElement : IBufferElementData, ISerializable
    {
        public Entity m_Vehicle;
        public uint m_OpenFrame;
        public byte m_SectionKind;
        public int m_FromStationSakIndex;
        public int m_ToStationSakIndex;
        public int m_FromStationOccurrence;
        public int m_ToStationOccurrence;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Vehicle); writer.Write(m_OpenFrame); writer.Write(m_SectionKind);
            writer.Write(m_FromStationSakIndex); writer.Write(m_ToStationSakIndex);
            writer.Write(m_FromStationOccurrence); writer.Write(m_ToStationOccurrence);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Vehicle); reader.Read(out m_OpenFrame); reader.Read(out m_SectionKind);
            reader.Read(out m_FromStationSakIndex); reader.Read(out m_ToStationSakIndex);
            reader.Read(out m_FromStationOccurrence); reader.Read(out m_ToStationOccurrence);
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowStopElement : IBufferElementData, ISerializable
    {
        public Entity m_Vehicle;
        public Entity m_Line;
        public int m_Mode;
        public FixedString128Bytes m_LineId;
        public int m_WaypointIndex;
        public int m_StationSakIndex;
        public uint m_OpenFrame;
        public Entity m_OpenStop;
        public Entity m_OpenWaypoint;
        public int m_StationOccurrence;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Vehicle); writer.Write(m_Line); writer.Write(m_Mode); writer.Write(m_LineId.ToString());
            writer.Write(m_WaypointIndex); writer.Write(m_StationSakIndex); writer.Write(m_OpenFrame);
            writer.Write(m_OpenStop); writer.Write(m_OpenWaypoint); writer.Write(m_StationOccurrence);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Vehicle); reader.Read(out m_Line); reader.Read(out m_Mode); reader.Read(out string lineId);
            m_LineId = lineId; reader.Read(out m_WaypointIndex); reader.Read(out m_StationSakIndex); reader.Read(out m_OpenFrame);
            reader.Read(out m_OpenStop); reader.Read(out m_OpenWaypoint); reader.Read(out m_StationOccurrence);
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowTripElement : IBufferElementData, ISerializable
    {
        public Entity m_Passenger;
        public Entity m_Vehicle;
        public Entity m_PreviousVehicle;
        public int m_Mode;
        public int m_OriginStationSakIndex;
        public int m_AlightStationSakIndex;
        public Entity m_FromStop;
        public Entity m_FromWaypoint;
        public int m_FromWaypointIndex;
        public int m_FromStationOccurrence;
        public int m_DayIndex;
        public int m_BucketMinute;
        public uint m_AlightFrame;
        public uint m_OpenFrame;
        public uint m_RemainingExpiryFrames;
        public int m_Generation;
        public byte m_Pending;
        public FixedString128Bytes m_FirstLineId;
        public FixedString128Bytes m_CurrentLineId;
        public int m_RawPurpose;
        public byte m_PurposeCategory;
        public int m_OriginRawPurpose;
        public byte m_OriginPurposeCategory;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Passenger); writer.Write(m_Vehicle); writer.Write(m_PreviousVehicle);
            writer.Write(m_Mode); writer.Write(m_OriginStationSakIndex); writer.Write(m_AlightStationSakIndex);
            writer.Write(m_FromStop); writer.Write(m_FromWaypoint); writer.Write(m_FromWaypointIndex);
            writer.Write(m_FromStationOccurrence);
            writer.Write(m_DayIndex); writer.Write(m_BucketMinute);
            writer.Write(m_AlightFrame); writer.Write(m_OpenFrame); writer.Write(m_RemainingExpiryFrames);
            writer.Write(m_Generation); writer.Write(m_Pending);
            writer.Write(m_FirstLineId.ToString()); writer.Write(m_CurrentLineId.ToString());
            writer.Write(m_RawPurpose); writer.Write(m_PurposeCategory);
            writer.Write(m_OriginRawPurpose); writer.Write(m_OriginPurposeCategory);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Passenger); reader.Read(out m_Vehicle); reader.Read(out m_PreviousVehicle);
            reader.Read(out m_Mode); reader.Read(out m_OriginStationSakIndex); reader.Read(out m_AlightStationSakIndex);
            reader.Read(out m_FromStop); reader.Read(out m_FromWaypoint); reader.Read(out m_FromWaypointIndex);
            reader.Read(out m_FromStationOccurrence);
            reader.Read(out m_DayIndex); reader.Read(out m_BucketMinute);
            reader.Read(out m_AlightFrame); reader.Read(out m_OpenFrame); reader.Read(out m_RemainingExpiryFrames);
            reader.Read(out m_Generation); reader.Read(out m_Pending);
            reader.Read(out string firstLineId); reader.Read(out string currentLineId);
            m_FirstLineId = firstLineId; m_CurrentLineId = currentLineId;
            reader.Read(out m_RawPurpose); reader.Read(out m_PurposeCategory);
            reader.Read(out m_OriginRawPurpose); reader.Read(out m_OriginPurposeCategory);
        }
    }

    [InternalBufferCapacity(0)]
    public struct PassengerFlowPlanElement : IBufferElementData, ISerializable
    {
        public Entity m_Passenger;
        public Entity m_Vehicle;
        public uint m_OpenFrame;
        public byte m_NextKind;
        public int m_BoardStationSakIndex;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Passenger); writer.Write(m_Vehicle); writer.Write(m_OpenFrame);
            writer.Write(m_NextKind); writer.Write(m_BoardStationSakIndex);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Passenger); reader.Read(out m_Vehicle); reader.Read(out m_OpenFrame);
            reader.Read(out m_NextKind); reader.Read(out m_BoardStationSakIndex);
        }
    }
}
