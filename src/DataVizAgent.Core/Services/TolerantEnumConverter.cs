using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataVizAgent.Services;

/// <summary>
/// Enum converter for agent-emitted JSON that never throws on unknown values. Local models
/// sometimes invent options (a chart "type" this app doesn't have, an aggregation alias, …);
/// with the strict converter one bad value discards the entire tool call and the raw JSON block
/// leaks into chat as text. Falling back to the enum's default keeps the rest of the call usable.
/// </summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(reader.GetString(), ignoreCase: true, out TEnum parsed) &&
                Enum.IsDefined(parsed))
            {
                return parsed;
            }

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out int number) &&
                Enum.IsDefined(typeof(TEnum), number))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), number);
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
