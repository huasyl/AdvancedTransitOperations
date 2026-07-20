using System.Runtime.Serialization;

#pragma warning disable CS0649

namespace RapidTransitMod.PassengerFlow
{
    [DataContract]
    internal sealed class FlowSnapshotDto
    {
        [DataMember] public int schemaVersion;
        [DataMember] public string mode = string.Empty;
        [DataMember] public uint generatedAtFrame;
        [DataMember] public int bucketMinutes;
        [DataMember] public StationVolumeDto[] stationVolumes;
        [DataMember] public SectionVolumeDto[] sectionVolumes;
        [DataMember] public OdFlowDto[] odFlows;
        [DataMember] public StationCatalogDto[] stationCatalog;
        [DataMember] public WarningDto[] warnings;
    }

    [DataContract]
    internal sealed class StationCatalogDto
    {
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationName = string.Empty;
    }

    [DataContract]
    internal sealed class StationVolumeDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationName = string.Empty;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public int waitingPassengers;
        [DataMember] public int throughPassengers;
        [DataMember] public int serviceDayKey;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class SectionVolumeDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public int averageLoadPassengers;
        [DataMember] public int sampleCount;
        [DataMember] public int serviceDayKey;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class OdFlowDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string firstLineId = string.Empty;
        [DataMember] public string lastLineId = string.Empty;
        [DataMember] public string originStationId = string.Empty;
        [DataMember] public string destinationStationId = string.Empty;
        [DataMember] public int completedCount;
        [DataMember] public int serviceDayKey;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class WarningDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string code = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public int count;
        [DataMember] public uint lastFrame;
        [DataMember] public int serviceDayKey;
        [DataMember] public int bucketStartMinute;
    }
}

#pragma warning restore CS0649
