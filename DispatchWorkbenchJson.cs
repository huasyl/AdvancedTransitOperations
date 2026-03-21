using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RapidTransitMod
{
    internal static class DispatchWorkbenchJson
    {
        public static string Serialize<T>(T value)
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

        public static T Deserialize<T>(string json)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(json) ? "{}" : json)))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }
    }
}
