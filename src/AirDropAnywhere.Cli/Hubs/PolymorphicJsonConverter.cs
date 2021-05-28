using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirDropAnywhere.Cli.Hubs
{
    /// <summary>
    /// Converter that ensures that the provided types are serialized/deserialized
    /// in a way that maintains type information over the wire.
    /// </summary>
    internal class PolymorphicJsonConverter : JsonConverter<object>
    {
        private readonly ImmutableDictionary<string, Type> _forwardMappings;
        private readonly ImmutableDictionary<Type, string> _reverseMappings;
        
        public PolymorphicJsonConverter(
            IEnumerable<(string Name, Type Type)> typeMappings
        )
        {
            if (typeMappings == null)
            {
                throw new ArgumentNullException(nameof(typeMappings));
            }

            var forwardMappings = ImmutableDictionary.CreateBuilder<string, Type>();
            var reverseMappings = ImmutableDictionary.CreateBuilder<Type, string>();
            foreach (var typeMapping in typeMappings)
            {
                forwardMappings[typeMapping.Name] = typeMapping.Type;
                reverseMappings[typeMapping.Type] = typeMapping.Name;
            }

            _forwardMappings = forwardMappings.ToImmutableDictionary();
            _reverseMappings = reverseMappings.ToImmutableDictionary();
        }
        
        public override bool CanConvert(Type type) => _reverseMappings.ContainsKey(type);
        
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            var typeName = reader.GetString();
            if (typeName == null || !_forwardMappings.TryGetValue(typeName, out var type))
            {
                throw new JsonException($"Unsupported type '{typeName}'");
            }
            
            var value = JsonSerializer.Deserialize(ref reader, type)!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException();
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            if (!_reverseMappings.TryGetValue(type, out var typeName))
            {
                throw new JsonException($"Unsupported type '{type}'");
            }
            
            writer.WriteStartObject();
            writer.WritePropertyName(typeName);
            JsonSerializer.Serialize(writer, value);
            writer.WriteEndObject();
        }

        public static PolymorphicJsonConverter Create(Type rootType) =>
            new(
                rootType
                    .GetCustomAttributes<PolymorphicJsonIncludeAttribute>()
                    .Select(attr => (attr.Name, attr.Type))
                    .Concat(new[] { ("root", rootType) })
            );
    }
}