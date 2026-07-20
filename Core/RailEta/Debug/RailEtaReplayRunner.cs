#if RT_DEBUG_TOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using Unity.Entities;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    public sealed class RailEtaReplayResult
    {
        public string Format { get; set; } = "rail-eta-replay-result-v1";
        public string SourceHotAssemblyVersion { get; set; } = string.Empty;
        public string SourcePredictorBuildId { get; set; } = string.Empty;
        public string ReplayHotAssemblyVersion { get; set; } = string.Empty;
        public RailEtaPrediction OriginalPrediction { get; set; }
        public RailEtaPrediction ReplayPrediction { get; set; }
    }

    public static class RailEtaReplayRunner
    {
        public static string Replay(string replayPath, string hotOutputPath)
        {
            if (String.IsNullOrWhiteSpace(replayPath)) throw new ArgumentException("Replay path is required.", nameof(replayPath));
            string input = Path.GetFullPath(replayPath);
            if (!File.Exists(input)) throw new FileNotFoundException("Rail ETA replay package is missing.", input);

            RailEtaReplayPackage package = RailEtaReplayJson.Read(input);
            if (package?.FrozenWorld == null || package.Snapshot == null || package.Request == null)
                throw new InvalidDataException("Replay package does not contain FrozenWorld, Snapshot, and Request.");

            RailEtaPrediction replay = new RailPredictionSolver().Predict(
                package.FrozenWorld,
                package.Snapshot,
                package.Request,
                new RailEtaWorkspace(),
                new RailEtaCancellation(null));
            var result = new RailEtaReplayResult
            {
                SourceHotAssemblyVersion = package.HotAssemblyVersion,
                SourcePredictorBuildId = package.PredictorBuildId,
                ReplayHotAssemblyVersion = typeof(RailEtaReplayRunner).Assembly.GetName().Version?.ToString() ?? string.Empty,
                OriginalPrediction = package.Prediction,
                ReplayPrediction = replay
            };
            string output = String.IsNullOrWhiteSpace(hotOutputPath)
                ? Path.Combine(Path.GetDirectoryName(input) ?? string.Empty, Path.GetFileNameWithoutExtension(input) + "-result.json")
                : Path.GetFullPath(hotOutputPath);
            string directory = Path.GetDirectoryName(output);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var resultSerializer = new DataContractJsonSerializer(typeof(RailEtaReplayResult), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 10000000 });
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read)) resultSerializer.WriteObject(stream, result);
            return output;
        }
    }

    internal static class RailEtaReplayJson
    {
        private static readonly JsonSerializerSettings s_Settings = new JsonSerializerSettings
        {
            ContractResolver = new RailEtaReplayContractResolver(),
            Converters = new List<JsonConverter> { new EntityKeyDictionaryConverter() },
            Formatting = Formatting.None,
            MaxDepth = 256
        };

        internal static void Write(string path, RailEtaReplayPackage package)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            using (var json = new JsonTextWriter(writer))
                Newtonsoft.Json.JsonSerializer.Create(s_Settings).Serialize(json, package);
        }

        internal static RailEtaReplayPackage Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream))
            using (var json = new JsonTextReader(reader))
                return Newtonsoft.Json.JsonSerializer.Create(s_Settings).Deserialize<RailEtaReplayPackage>(json);
        }
    }

    internal sealed class EntityKeyDictionaryConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType)
        {
            Type dictionary = FindDictionaryType(objectType);
            return dictionary != null && dictionary.GetGenericArguments()[0] == typeof(Entity);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            Type dictionaryType = FindDictionaryType(objectType);
            if (dictionaryType == null) throw new JsonSerializationException("Entity-key dictionary type is invalid.");
            Type valueType = dictionaryType.GetGenericArguments()[1];
            Type concreteType = objectType.IsInterface || objectType.IsAbstract
                ? typeof(Dictionary<,>).MakeGenericType(typeof(Entity), valueType)
                : objectType;
            var result = (IDictionary)(existingValue ?? Activator.CreateInstance(concreteType));
            JObject source = JObject.Load(reader);
            foreach (JProperty property in source.Properties())
            {
                Entity key = ParseEntity(property.Name);
                object value = property.Value.ToObject(valueType, serializer);
                result[key] = value;
            }
            return result;
        }

        public override void WriteJson(JsonWriter writer, object value,
            Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();

        private static Type FindDictionaryType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)) return type;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return type;
            return type.GetInterfaces().FirstOrDefault(value => value.IsGenericType
                && value.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        private static Entity ParseEntity(string text)
        {
            if (String.Equals(text, "Entity.Null", StringComparison.Ordinal)) return Entity.Null;
            string value = text ?? String.Empty;
            if (value.StartsWith("Entity(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
                value = value.Substring(7, value.Length - 8);
            int separator = value.LastIndexOf(':');
            if (separator <= 0
                || !Int32.TryParse(value.Substring(0, separator), out int index)
                || !Int32.TryParse(value.Substring(separator + 1), out int version))
                throw new JsonSerializationException("Invalid Entity dictionary key: " + text);
            return new Entity { Index = index, Version = version };
        }
    }

    internal sealed class RailEtaReplayContractResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            string ns = type.Namespace ?? string.Empty;
            if (!type.IsValueType || (!ns.StartsWith("Unity.", StringComparison.Ordinal)
                && !ns.StartsWith("Game.", StringComparison.Ordinal)
                && !ns.StartsWith("Colossal.", StringComparison.Ordinal)))
                return base.CreateProperties(type, memberSerialization);

            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .Select(field =>
                {
                    JsonProperty property = base.CreateProperty(field, MemberSerialization.Fields);
                    property.Readable = true;
                    property.Writable = true;
                    return property;
                })
                .ToList();
        }
    }
}
#endif
