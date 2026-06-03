using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RapidTransitMod
{
    internal struct RtVehicleRequestSentinel : IComponentData, IEmptySerializable
    {
    }

    internal struct RtSpawnPermitRequest : IComponentData, IEmptySerializable
    {
    }
}
