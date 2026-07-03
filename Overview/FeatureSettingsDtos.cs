using System.Runtime.Serialization;

namespace RapidTransitMod.Overview
{
    [DataContract]
    internal sealed class OverviewFeatureSettingsRequestDto
    {
        [DataMember]
        public global::RapidTransitMod.RuntimeFeatureSettingsDto featureSettings = null!;
    }

    [DataContract]
    internal sealed class OverviewFeatureSettingsResultDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string[] errors;
        [DataMember]
        public string version;
        [DataMember]
        public global::RapidTransitMod.RuntimeFeatureSettingsDto featureSettings;
    }

    [DataContract]
    internal sealed class OverviewFeatureSettingsOperationStatusDto
    {
        [DataMember]
        public bool success;
        [DataMember]
        public string operationId;
        [DataMember]
        public string state;
        [DataMember]
        public string error;
        [DataMember]
        public OverviewFeatureSettingsResultDto result;
    }
}
