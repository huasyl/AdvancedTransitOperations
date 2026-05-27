using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RapidTransitMod
{
    internal struct RtVehicleRequestSentinel : IComponentData, ISerializable
    {
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
        }
    }

    internal struct RtSpawnPermitRequest : IComponentData, ISerializable
    {
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
        }
    }
}
