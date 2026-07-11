using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

internal static class StationJobPersistenceJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new StationJobIdJsonConverter());
        options.Converters.Add(new StationOperationRunIdJsonConverter());
        return options;
    }

    private sealed class StationJobIdJsonConverter : JsonConverter<StationJobId>
    {
        public override StationJobId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(Guid.Parse(reader.GetString()
                ?? throw new JsonException("Station job id cannot be null.")));

        public override void Write(
            Utf8JsonWriter writer,
            StationJobId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class StationOperationRunIdJsonConverter : JsonConverter<StationOperationRunId>
    {
        public override StationOperationRunId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString()
                ?? throw new JsonException("Station operation run id cannot be null."));

        public override void Write(
            Utf8JsonWriter writer,
            StationOperationRunId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
