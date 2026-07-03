using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RapidTransitMod
{
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
}
