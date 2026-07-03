using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RapidTransitMod.Workbenches
{
    internal static class Json
    {
        internal static string Write<T>(T value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        internal static T Read<T>(string json)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(json) ? "{}" : json)))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }
    }
}
