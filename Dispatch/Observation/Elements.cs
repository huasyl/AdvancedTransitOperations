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
        public FixedString128Bytes m_ObservationKey;
        public float m_AverageFrames;
        public int m_SampleCount;
        public uint m_LastObservedFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_ObservationKey.ToString());
            writer.Write(m_AverageFrames);
            writer.Write(m_SampleCount);
            writer.Write(m_LastObservedFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string observationKey);
            m_ObservationKey = observationKey ?? string.Empty;
            reader.Read(out m_AverageFrames);
            reader.Read(out m_SampleCount);
            reader.Read(out m_LastObservedFrame);
        }
    }

    [InternalBufferCapacity(1)]
    public struct BusSegObservationElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public Entity m_FromWaypointEntity;
        public Entity m_FromStopEntity;
        public Entity m_ToWaypointEntity;
        public Entity m_ToStopEntity;
        public float m_EstimatedFrames;
        public int m_SampleCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_FromWaypointEntity);
            writer.Write(m_FromStopEntity);
            writer.Write(m_ToWaypointEntity);
            writer.Write(m_ToStopEntity);
            writer.Write(m_EstimatedFrames);
            writer.Write(m_SampleCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_FromWaypointEntity);
            reader.Read(out m_FromStopEntity);
            reader.Read(out m_ToWaypointEntity);
            reader.Read(out m_ToStopEntity);
            reader.Read(out m_EstimatedFrames);
            reader.Read(out m_SampleCount);
        }
    }

    [InternalBufferCapacity(1)]
    public struct BusRouteSnapshotElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public int m_Order;
        public Entity m_WaypointEntity;
        public Entity m_ResolvedStopEntity;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_Order);
            writer.Write(m_WaypointEntity);
            writer.Write(m_ResolvedStopEntity);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_Order);
            reader.Read(out m_WaypointEntity);
            reader.Read(out m_ResolvedStopEntity);
        }
    }

    [InternalBufferCapacity(0)]
    public struct RailSegmentObservationElement : IBufferElementData, ISerializable
    {
        public Entity m_LineEntity;
        public Entity m_FromWaypointEntity;
        public Entity m_FromStopEntity;
        public Entity m_ToWaypointEntity;
        public Entity m_ToStopEntity;
        public float m_AverageFrames;
        public int m_SampleCount;
        public uint m_LastObservedFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_LineEntity);
            writer.Write(m_FromWaypointEntity);
            writer.Write(m_FromStopEntity);
            writer.Write(m_ToWaypointEntity);
            writer.Write(m_ToStopEntity);
            writer.Write(m_AverageFrames);
            writer.Write(m_SampleCount);
            writer.Write(m_LastObservedFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_LineEntity);
            reader.Read(out m_FromWaypointEntity);
            reader.Read(out m_FromStopEntity);
            reader.Read(out m_ToWaypointEntity);
            reader.Read(out m_ToStopEntity);
            reader.Read(out m_AverageFrames);
            reader.Read(out m_SampleCount);
            reader.Read(out m_LastObservedFrame);
        }
    }

    [InternalBufferCapacity(0)]
    public struct MonitorAverageLineElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public Entity m_Line;
        public FixedString64Bytes m_StopSig;
        public ulong m_Revision;
        public int m_SegmentCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_Line);
            writer.Write(m_StopSig.ToString());
            writer.Write(m_Revision);
            writer.Write(m_SegmentCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Line);
            reader.Read(out string stopSig);
            m_StopSig = stopSig ?? string.Empty;
            reader.Read(out m_Revision);
            reader.Read(out m_SegmentCount);
        }
    }

    [InternalBufferCapacity(0)]
    public struct MonitorAverageSegmentElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public Entity m_Line;
        public int m_Order;
        public ulong m_TotalFrames;
        public int m_SampleCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_Line);
            writer.Write(m_Order);
            if (m_Version >= 2)
                writer.Write(m_TotalFrames);
            else
                writer.Write(0);
            writer.Write(m_SampleCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_Line);
            reader.Read(out m_Order);
            if (m_Version >= 2)
            {
                reader.Read(out m_TotalFrames);
            }
            else
            {
                reader.Read(out int ignored);
                m_TotalFrames = 0;
            }
            reader.Read(out m_SampleCount);
        }
    }

    [InternalBufferCapacity(0)]
    public struct MonitorDateSlotElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public int m_DateKey;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_DateKey);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_DateKey);
        }
    }

    [InternalBufferCapacity(1)]
    public struct MonitorIntegrityElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public int m_DataComplete;
        public int m_DroppedTripCount;
        public int m_PersistenceHealthy;
        public FixedString64Bytes m_LastIssueCode;
        public int m_IssueCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_DataComplete);
            writer.Write(m_DroppedTripCount);
            writer.Write(m_PersistenceHealthy);
            writer.Write(m_LastIssueCode.ToString());
            writer.Write(m_IssueCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_DataComplete);
            reader.Read(out m_DroppedTripCount);
            reader.Read(out m_PersistenceHealthy);
            reader.Read(out string issueCode);
            m_LastIssueCode = issueCode ?? string.Empty;
            reader.Read(out m_IssueCount);
        }
    }

    [InternalBufferCapacity(0)]
    public struct MonitorTripElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public int m_TripOrder;
        public int m_Active;
        public FixedString512Bytes m_Key;
        public FixedString64Bytes m_LineKey;
        public FixedString64Bytes m_LineId;
        public FixedString512Bytes m_RowId;
        public FixedString64Bytes m_ServiceKind;
        public FixedString64Bytes m_StopSig;
        public Entity m_Line;
        public Entity m_Vehicle;
        public int m_ServiceDateKey;
        public int m_SlotMinute;
        public int m_NextArrivalOrder;
        public int m_VisibleStopCount;
        public int m_SuppressPlanFrom;
        public int m_State;
        public int m_EndReason;
        public uint m_LaunchFrame;
        public uint m_UpdatedFrame;
        public int m_StopCount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_TripOrder);
            writer.Write(m_Active);
            writer.Write(m_Key.ToString());
            writer.Write(m_LineKey.ToString());
            writer.Write(m_LineId.ToString());
            writer.Write(m_RowId.ToString());
            writer.Write(m_ServiceKind.ToString());
            writer.Write(m_StopSig.ToString());
            writer.Write(m_Line);
            writer.Write(m_Vehicle);
            writer.Write(m_ServiceDateKey);
            writer.Write(m_SlotMinute);
            if (m_Version == 1)
                writer.Write(0);
            writer.Write(m_NextArrivalOrder);
            writer.Write(m_VisibleStopCount);
            writer.Write(m_SuppressPlanFrom);
            writer.Write(m_State);
            if (m_Version >= 3)
                writer.Write(m_EndReason);
            writer.Write(m_LaunchFrame);
            writer.Write(m_UpdatedFrame);
            writer.Write(m_StopCount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_TripOrder);
            reader.Read(out m_Active);
            reader.Read(out string key);
            m_Key = key ?? string.Empty;
            reader.Read(out string lineKey);
            m_LineKey = lineKey ?? string.Empty;
            reader.Read(out string lineId);
            m_LineId = lineId ?? string.Empty;
            reader.Read(out string rowId);
            m_RowId = rowId ?? string.Empty;
            reader.Read(out string serviceKind);
            m_ServiceKind = serviceKind ?? string.Empty;
            reader.Read(out string stopSig);
            m_StopSig = stopSig ?? string.Empty;
            reader.Read(out m_Line);
            reader.Read(out m_Vehicle);
            reader.Read(out m_ServiceDateKey);
            reader.Read(out m_SlotMinute);
            if (m_Version == 1)
                reader.Read(out int legacyActualStartMinute);
            reader.Read(out m_NextArrivalOrder);
            reader.Read(out m_VisibleStopCount);
            reader.Read(out m_SuppressPlanFrom);
            reader.Read(out m_State);
            m_EndReason = 0;
            if (m_Version >= 3)
                reader.Read(out m_EndReason);
            reader.Read(out m_LaunchFrame);
            reader.Read(out m_UpdatedFrame);
            reader.Read(out m_StopCount);
        }
    }

    [InternalBufferCapacity(0)]
    public struct MonitorStopElement : IBufferElementData, ISerializable
    {
        public int m_Version;
        public int m_TripOrder;
        public int m_StopOrder;
        public FixedString128Bytes m_StopKey;
        public Entity m_Station;
        public int m_WaypointIndex;
        public int m_PlannedArrival;
        public int m_PlannedDeparture;
        public int m_ActualArrival;
        public int m_ActualDeparture;
        public uint m_ActualArrivalFrame;
        public uint m_ActualDepartureFrame;
        public uint m_OpenIntervalMaxFrames;
        public int m_Skipped;
        public int m_Cleared;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Version);
            writer.Write(m_TripOrder);
            writer.Write(m_StopOrder);
            writer.Write(m_StopKey.ToString());
            writer.Write(m_Station);
            writer.Write(m_WaypointIndex);
            writer.Write(m_PlannedArrival);
            writer.Write(m_PlannedDeparture);
            writer.Write(m_ActualArrival);
            writer.Write(m_ActualDeparture);
            if (m_Version >= 2)
            {
                writer.Write(m_ActualArrivalFrame);
                writer.Write(m_ActualDepartureFrame);
                writer.Write(m_OpenIntervalMaxFrames);
            }
            if (m_Version >= 3)
                writer.Write(m_Skipped);
            writer.Write(m_Cleared);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Version);
            reader.Read(out m_TripOrder);
            reader.Read(out m_StopOrder);
            reader.Read(out string stopKey);
            m_StopKey = stopKey ?? string.Empty;
            reader.Read(out m_Station);
            reader.Read(out m_WaypointIndex);
            reader.Read(out m_PlannedArrival);
            reader.Read(out m_PlannedDeparture);
            reader.Read(out m_ActualArrival);
            reader.Read(out m_ActualDeparture);
            if (m_Version >= 2)
            {
                reader.Read(out m_ActualArrivalFrame);
                reader.Read(out m_ActualDepartureFrame);
                reader.Read(out m_OpenIntervalMaxFrames);
            }
            else
            {
                m_ActualArrivalFrame = 0u;
                m_ActualDepartureFrame = 0u;
                m_OpenIntervalMaxFrames = 0u;
            }
            m_Skipped = 0;
            if (m_Version >= 3)
                reader.Read(out m_Skipped);
            reader.Read(out m_Cleared);
        }
    }
}
