using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class RuntimeMonitoringPersistenceJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = RuntimePersistenceJson.CreateOptions();
        options.Converters.Add(new NonEmptyGuidJsonConverter());
        options.Converters.Add(new RoundtripDateTimeOffsetJsonConverter());
        options.Converters.Add(new GuidIdentifierJsonConverter<ProductionRunId>(
            value => new ProductionRunId(value),
            value => value.Value));
        options.Converters.Add(new GuidIdentifierJsonConverter<RuntimeSessionId>(
            value => new RuntimeSessionId(value),
            value => value.Value));
        options.Converters.Add(new GuidIdentifierJsonConverter<RuntimeIncidentId>(
            value => new RuntimeIncidentId(value),
            value => value.Value));
        options.Converters.Add(new CanonicalEnumJsonConverter<RuntimeSessionStatus>());
        options.Converters.Add(new CanonicalEnumJsonConverter<RuntimeCommandStatus>());
        options.Converters.Add(new CanonicalEnumJsonConverter<RuntimeIncidentSeverity>());
        return options;
    }

    private sealed class GuidIdentifierJsonConverter<TIdentifier>(
        Func<Guid, TIdentifier> create,
        Func<TIdentifier, Guid> getValue) : JsonConverter<TIdentifier>
    {
        public override TIdentifier Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !Guid.TryParseExact(reader.GetString(), "D", out var value)
                || value == Guid.Empty)
            {
                throw new JsonException(
                    $"Persisted Runtime monitoring {typeof(TIdentifier).Name} must be a non-empty canonical GUID.");
            }

            return create(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            TIdentifier value,
            JsonSerializerOptions options)
        {
            var identifier = getValue(value);
            if (identifier == Guid.Empty)
            {
                throw new JsonException(
                    $"Runtime monitoring {typeof(TIdentifier).Name} cannot be empty.");
            }

            writer.WriteStringValue(identifier.ToString("D"));
        }
    }

    private sealed class CanonicalEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (value is not null && CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
            {
                return parsed;
            }

            throw new JsonException(
                $"Persisted Runtime monitoring {typeof(TEnum).Name} must be one of: "
                + CanonicalEnumToken.ExpectedTokens<TEnum>() + ".");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException(
                    $"Runtime monitoring {typeof(TEnum).Name} value '{value}' is invalid.");
            }

            writer.WriteStringValue(value.ToString());
        }
    }

    private sealed class NonEmptyGuidJsonConverter : JsonConverter<Guid>
    {
        public override Guid Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Guid.TryParseExact(reader.GetString(), "D", out var value)
                && value != Guid.Empty)
            {
                return value;
            }

            throw new JsonException(
                "Persisted Runtime monitoring GUID must be non-empty and canonical.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            Guid value,
            JsonSerializerOptions options)
        {
            if (value == Guid.Empty)
            {
                throw new JsonException("Runtime monitoring GUID cannot be empty.");
            }

            writer.WriteStringValue(value.ToString("D"));
        }
    }

    private sealed class RoundtripDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParseExact(
                    reader.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var value))
            {
                return value;
            }

            throw new JsonException(
                "Persisted Runtime monitoring timestamp must use the round-trip format.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
