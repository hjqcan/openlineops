using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationLifecyclePersistenceJson
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(StationLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(
            new StationLifecycleSnapshotDocument(
                CurrentSchemaVersion,
                snapshot),
            Options);
    }

    public static StationLifecycleSnapshot Deserialize(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new InvalidDataException(
                "Persisted Station lifecycle document cannot be empty.");
        }

        using var parsed = JsonDocument.Parse(document);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Persisted Station lifecycle document must be a JSON object.");
        }

        if (!parsed.RootElement.TryGetProperty("schemaVersion", out _))
        {
            return JsonSerializer.Deserialize<StationLifecycleSnapshot>(
                    document,
                    Options)
                ?? throw new InvalidDataException(
                    "Persisted legacy Station lifecycle document has no snapshot.");
        }

        var persisted = JsonSerializer.Deserialize<StationLifecycleSnapshotDocument>(
                document,
                Options)
            ?? throw new InvalidDataException(
                "Persisted Station lifecycle document has no snapshot.");
        if (persisted.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Station lifecycle schema version "
                + $"{persisted.SchemaVersion} is unsupported.");
        }

        return persisted.Snapshot;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = RuntimePersistenceJson.CreateOptions();
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }

    private sealed record StationLifecycleSnapshotDocument(
        int SchemaVersion,
        StationLifecycleSnapshot Snapshot);
}
