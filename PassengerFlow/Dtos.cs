using System.Runtime.Serialization;

#pragma warning disable CS0649

namespace RapidTransitMod.PassengerFlow
{
    [DataContract]
    internal sealed class FlowSnapshotDto
    {
        [DataMember] public int schemaVersion;
        [DataMember] public StationGroupDto[] stationGroups;
        [DataMember] public string mode = string.Empty;
        [DataMember] public string view;
        [DataMember] public string day;
        [DataMember] public int selectedDayIndex;
        [DataMember] public string sectionKind;
        [DataMember] public FlowBucketDto requestedFromBucket;
        [DataMember] public FlowBucketDto requestedToBucket;
        [DataMember] public uint generatedAtFrame;
        [DataMember] public int bucketMinutes;
        [DataMember] public StationVolumeDto[] stationVolumes;
        [DataMember] public SectionVolumeDto[] sectionVolumes;
        [DataMember] public OdFlowDto[] odFlows;
        [DataMember] public StationCatalogDto[] stationCatalog;
        [DataMember] public WarningDto[] warnings;
        [DataMember] public TransferFlowDto[] transferFlows;
        [DataMember] public StationWaitingDto[] stationWaiting;
        [DataMember] public LineTimeLoadDto[] lineTimeLoads;
        [DataMember] public NetworkTimeLoadDto[] networkTimeLoads;
        [DataMember] public int currentDayIndex;
        [DataMember] public int currentMinute;
        [DataMember] public bool samplingReady;
        [DataMember] public FlowBucketDto retainedFromBucket;
        [DataMember] public FlowBucketDto retainedToBucket;
        [DataMember] public FlowBucketDto effectiveFromBucket;
        [DataMember] public FlowBucketDto effectiveToBucket;
        [DataMember] public LineStopDto[] lineStops;
        [DataMember] public bool lineStopsAvailable;
        [DataMember] public FlowSummaryDto summary;
    }

    [DataContract]
    internal sealed class StationGroupDto
    {
        [DataMember] public string stationGroupId;
        [DataMember] public string stationName;
        [DataMember] public string[] memberStationIds;
    }

    [DataContract]
    internal sealed class StationCatalogDto
    {
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public string stationName = string.Empty;
    }

    [DataContract]
    internal sealed class StationVolumeDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public string stationName = string.Empty;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public int waitingPassengers;
        [DataMember] public int throughPassengers;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal class SectionVolumeDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string fromStationGroupId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public string toStationGroupId = string.Empty;
        [DataMember] public int averageLoadPassengers;
        [DataMember] public long loadPassengersSum;
        [DataMember] public long capacitySum;
        [DataMember] public long ratedLoadPassengersSum;
        [DataMember] public int capacitySampleCount;
        [DataMember] public double? loadRatio;
        [DataMember] public int sampleCount;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class OdFlowDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string firstLineId = string.Empty;
        [DataMember] public string lastLineId = string.Empty;
        [DataMember] public string originStationId = string.Empty;
        [DataMember] public string originStationGroupId = string.Empty;
        [DataMember] public string destinationStationId = string.Empty;
        [DataMember] public string destinationStationGroupId = string.Empty;
        [DataMember] public int completedCount;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class WarningDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string code = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public int count;
        [DataMember] public uint lastFrame;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class PurposeCountDto
    {
        [DataMember] public int? rawPurpose;
        [DataMember] public string category = string.Empty;
        [DataMember] public int? toRawPurpose;
        [DataMember] public string toCategory;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public long loadPassengersSum;
        [DataMember] public int completedCount;
    }

    [DataContract]
    internal sealed class TransferFlowDto
    {
        [DataMember] public string fromMode = string.Empty;
        [DataMember] public string fromLineId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string fromStationGroupId = string.Empty;
        [DataMember] public int fromStationOccurrence;
        [DataMember] public string toMode = string.Empty;
        [DataMember] public string toLineId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public string toStationGroupId = string.Empty;
        [DataMember] public int toStationOccurrence;
        [DataMember] public int completedCount;
        [DataMember] public double walkPathMetersSum;
        [DataMember] public int walkPathSampleCount;
        [DataMember] public long walkFramesSum;
        [DataMember] public int walkTimeSampleCount;
        [DataMember] public double? averageWalkMinutes;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class StationWaitingDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public int stationOccurrence;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public int latestWaitingCount;
        [DataMember] public int peakWaitingCount;
        [DataMember] public long waitingCountFrames;
        [DataMember] public double latestEstimatedWaitFrames;
        [DataMember] public double estimatedWaitFrameFrames;
        [DataMember] public long observedFrames;
        [DataMember] public double latestEstimatedWaitMinutes;
        [DataMember] public double observedGameMinutes;
        [DataMember] public int sampleCount;
        [DataMember] public double? averageWaitingCount;
        [DataMember] public double? averageEstimatedWaitMinutes;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class LineTimeLoadDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
        [DataMember] public long passengerFrames;
        [DataMember] public long observedFrames;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public long capacityObservedFrames;
        [DataMember] public double? averageOnboardPassengers;
        [DataMember] public double? averageLoadRatio;
    }

    [DataContract]
    internal class NetworkTimeSummaryDto
    {
        [DataMember] public string mode;
        [DataMember] public long passengerFrames;
        [DataMember] public long observedFrames;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public long capacityObservedFrames;
        [DataMember] public double? averageOnboardPassengers;
        [DataMember] public double? averageLoadRatio;
    }

    [DataContract]
    internal sealed class NetworkTimeLoadDto : NetworkTimeSummaryDto
    {
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class FlowQueryDto
    {
        [DataMember] public string mode;
        [DataMember] public string view;
        [DataMember] public string sectionKind;
        [DataMember] public int? stationRankLimit;
        [DataMember] public bool? includeTransfers;
        [DataMember] public bool? includeStationWaiting;
        [DataMember] public bool? includeLineComparison;
        [DataMember] public bool? includePurposes;
        [DataMember] public bool? includeSummary;
        [DataMember] public bool? includeSeries;
        [DataMember] public bool? includeSections;
        [DataMember] public bool? includeStationCatalog;
        [DataMember] public bool? includeLineStops;
        [DataMember] public string lineId;
        [DataMember] public string stationId;
        [DataMember] public string stationGroupId;
        [DataMember] public bool directoryOnly;
        [DataMember] public string day;
        [DataMember] public int? fromMinute;
        [DataMember] public int? toMinute;
    }

    [DataContract]
    internal sealed class FlowBucketDto
    {
        [DataMember] public int dayIndex;
        [DataMember] public int bucketStartMinute;
    }

    [DataContract]
    internal sealed class LineStopDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public int waypointIndex;
        [DataMember] public int stationOccurrence;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public string stationName = string.Empty;
        [DataMember] public string stopName = string.Empty;
    }

    [DataContract]
    internal sealed class FlowSummaryDto
    {
        [DataMember] public LineVolumeDto[] lineVolumes;
        [DataMember] public LineTimeSummaryDto[] lineTimeLoads;
        [DataMember] public NetworkTimeSummaryDto[] networkTimeLoads;
        [DataMember] public SectionSummaryDto[] sectionVolumes;
        [DataMember] public TransferSummaryDto[] transferFlows;
        [DataMember] public WaitingSummaryDto[] stationWaiting;
        [DataMember] public StationSummaryDto[] stationVolumes;
        [DataMember] public OdSummaryDto[] odFlows;
        [DataMember] public StationSummaryDto[] stationTotals;
        [DataMember] public StationSummaryDto[] stationRanking;
        [DataMember] public int stationCount;
        [DataMember] public long boardings;
        [DataMember] public long alightings;
        [DataMember] public long completedOdCount;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class LineVolumeDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public long boardings;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public double? averageLoadRatio;
    }

    [DataContract]
    internal sealed class LineTimeSummaryDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public long passengerFrames;
        [DataMember] public long observedFrames;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public long capacityObservedFrames;
        [DataMember] public double? averageOnboardPassengers;
        [DataMember] public double? averageLoadRatio;
    }

    [DataContract]
    internal sealed class SectionLineDto
    {
        [DataMember] public string lineId = string.Empty;
        [DataMember] public long loadPassengersSum;
        [DataMember] public int sampleCount;
        [DataMember] public long ratedLoadPassengersSum;
        [DataMember] public long capacitySum;
        [DataMember] public int capacitySampleCount;
        [DataMember] public double? averageLoadPassengers;
        [DataMember] public double? loadRatio;
    }

    [DataContract]
    internal class SectionSummaryDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string fromStationGroupId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public string toStationGroupId = string.Empty;
        [DataMember] public long loadPassengersSum;
        [DataMember] public int sampleCount;
        [DataMember] public long ratedLoadPassengersSum;
        [DataMember] public long capacitySum;
        [DataMember] public int capacitySampleCount;
        [DataMember] public double? averageLoadPassengers;
        [DataMember] public double? loadRatio;
        [DataMember] public PurposeCountDto[] purposeCounts;
        [DataMember] public SectionLineDto[] lineContributions;
    }

    [DataContract]
    internal sealed class TransferSummaryDto
    {
        [DataMember] public string fromMode = string.Empty;
        [DataMember] public string fromLineId = string.Empty;
        [DataMember] public string fromStationId = string.Empty;
        [DataMember] public string fromStationGroupId = string.Empty;
        [DataMember] public int fromStationOccurrence;
        [DataMember] public string toMode = string.Empty;
        [DataMember] public string toLineId = string.Empty;
        [DataMember] public string toStationId = string.Empty;
        [DataMember] public string toStationGroupId = string.Empty;
        [DataMember] public int toStationOccurrence;
        [DataMember] public int completedCount;
        [DataMember] public double walkPathMetersSum;
        [DataMember] public int walkPathSampleCount;
        [DataMember] public long walkFramesSum;
        [DataMember] public int walkTimeSampleCount;
        [DataMember] public double? averageWalkPathMeters;
        [DataMember] public double? averageWalkMinutes;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class WaitingSummaryDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public int stationOccurrence;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public long waitingCountFrames;
        [DataMember] public double estimatedWaitFrameFrames;
        [DataMember] public long observedFrames;
        [DataMember] public int sampleCount;
        [DataMember] public double? averageWaitingCount;
        [DataMember] public double? averageEstimatedWaitMinutes;
    }

    [DataContract]
    internal sealed class StationSummaryDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string lineId = string.Empty;
        [DataMember] public string stationId = string.Empty;
        [DataMember] public string stationGroupId = string.Empty;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public PurposeCountDto[] purposeCounts;
    }

    [DataContract]
    internal sealed class OdLineDto
    {
        [DataMember] public string firstLineId = string.Empty;
        [DataMember] public string lastLineId = string.Empty;
        [DataMember] public int completedCount;
    }

    [DataContract]
    internal sealed class OdSummaryDto
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string firstLineId = string.Empty;
        [DataMember] public string lastLineId = string.Empty;
        [DataMember] public string originStationId = string.Empty;
        [DataMember] public string originStationGroupId = string.Empty;
        [DataMember] public string destinationStationId = string.Empty;
        [DataMember] public string destinationStationGroupId = string.Empty;
        [DataMember] public int completedCount;
        [DataMember] public PurposeCountDto[] purposeCounts;
        [DataMember] public OdLineDto[] lineContributions;
        [DataMember] public string dominantLineId = string.Empty;
    }
}

#pragma warning restore CS0649
