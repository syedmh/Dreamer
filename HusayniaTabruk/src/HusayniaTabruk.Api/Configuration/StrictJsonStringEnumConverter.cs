using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HusayniaTabruk.Api.Configuration;

internal sealed class StrictJsonStringEnumConverter(JsonNamingPolicy namingPolicy) : JsonConverterFactory
{
    private readonly JsonStringEnumConverter _inner =
        new(namingPolicy, allowIntegerValues: false);

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        JsonConverter inner = _inner.CreateConverter(typeToConvert, options);
        Type converterType = typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert);
        object? converter = Activator.CreateInstance(
            converterType,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: [inner],
            culture: null);

        return (JsonConverter)(converter ??
            throw new InvalidOperationException("Unable to create the strict enum converter."));
    }

    private sealed class StrictEnumConverter<TEnum>(JsonConverter inner) : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly bool IsFlags =
            typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false);
        private static readonly ulong DefinedBits = Enum.GetValues<TEnum>()
            .Aggregate(0UL, static (bits, value) => bits | ToUInt64(value));
        private readonly JsonConverter<TEnum> _inner = (JsonConverter<TEnum>)inner;

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (!IsFlags &&
                reader.TokenType == JsonTokenType.String &&
                reader.GetString()?.Contains(',', StringComparison.Ordinal) == true)
            {
                throw new JsonException();
            }

            TEnum value = _inner.Read(ref reader, typeToConvert, options);
            if (!IsDefined(value))
            {
                throw new JsonException();
            }

            return value;
        }

        public override TEnum ReadAsPropertyName(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (!IsFlags &&
                reader.GetString()?.Contains(',', StringComparison.Ordinal) == true)
            {
                throw new JsonException();
            }

            TEnum value = _inner.ReadAsPropertyName(ref reader, typeToConvert, options);
            if (!IsDefined(value))
            {
                throw new JsonException();
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!IsDefined(value))
            {
                throw new JsonException();
            }

            _inner.Write(writer, value, options);
        }

        public override void WriteAsPropertyName(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!IsDefined(value))
            {
                throw new JsonException();
            }

            _inner.WriteAsPropertyName(writer, value, options);
        }

        private static bool IsDefined(TEnum value) =>
            IsFlags
                ? (ToUInt64(value) & ~DefinedBits) == 0
                : Enum.IsDefined(value);

        private static ulong ToUInt64(TEnum value) =>
            Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum))) switch
            {
                TypeCode.SByte => unchecked((ulong)Convert.ToSByte(value, CultureInfo.InvariantCulture)),
                TypeCode.Int16 => unchecked((ulong)Convert.ToInt16(value, CultureInfo.InvariantCulture)),
                TypeCode.Int32 => unchecked((ulong)Convert.ToInt32(value, CultureInfo.InvariantCulture)),
                TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                TypeCode.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
                TypeCode.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                TypeCode.UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                TypeCode.UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Unsupported enum underlying type."),
            };
    }
}
